using System.IO;
using Silo.Tasks;

namespace Silo.Tests.Tasks;

[NonParallelizable]
public class TaskTests
{
    private int _reveals;
    private readonly List<ITrackedTask> _toasts = [];

    [SetUp]
    public void SetUp()
    {
        TaskCenter.Reset();
        _reveals = 0;
        _toasts.Clear();
        TaskCenter.Reveal = () => _reveals++;
        TaskCenter.Toast  = t => _toasts.Add(t);
    }

    [TearDown]
    public void TearDown()
    {
        TaskCenter.Reset();
    }

    // ---- the contract's base ---------------------------------------------------------------------

    [Test]
    public void A_task_goes_from_queued_to_running_to_one_of_the_final_states()
    {
        var task = new TrackedTask("Saving x", "Silo file");
        Assert.That(task.State,    Is.EqualTo(TaskState.Queued));
        Assert.That(task.Duration, Is.EqualTo(TimeSpan.Zero));

        task.Start();
        task.Report("2 of 5", 2, 100);
        Assert.That((task.State, task.Message, task.ResultCount, task.ResultBytes), Is.EqualTo((TaskState.Running, "2 of 5", 2, 100L)));
        Assert.That(task.StartedUtc, Is.Not.Null);

        task.Succeed("done", 5, 500);
        Assert.That((task.State, task.Message, task.ResultCount, task.ResultBytes), Is.EqualTo((TaskState.Succeeded, "done", 5, 500L)));
        Assert.That(task.EndedUtc, Is.Not.Null);

        // an ended task stays as it ended
        task.Fail("too late");
        task.Report("nope", 9);
        Assert.That((task.State, task.ResultCount), Is.EqualTo((TaskState.Succeeded, 5)));
    }

    [Test]
    public void The_duration_runs_while_the_task_does_and_stops_when_it_ends()
    {
        var task = new TrackedTask("t", "s");
        task.Start();
        Thread.Sleep(30);
        var running = task.Duration;
        task.Succeed();
        var ended = task.Duration;
        Thread.Sleep(30);

        Assert.That(running, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(task.Duration, Is.EqualTo(ended));
    }

    [Test]
    public void A_failure_keeps_its_message_and_a_success_without_one_keeps_the_last_progress()
    {
        var failed = new TrackedTask("a", "s");
        failed.Start();
        failed.Fail(new IOException("disk full"));
        Assert.That((failed.State, failed.Message), Is.EqualTo((TaskState.Failed, "disk full")));

        var ok = new TrackedTask("b", "s");
        ok.Report("3 of 3");
        ok.Succeed();
        Assert.That(ok.Message, Is.EqualTo("3 of 3"));
    }

    [Test]
    public void Only_a_task_that_can_be_cancelled_is_cancelled_and_only_while_it_has_not_ended()
    {
        var fixedTask = new TrackedTask("fixed", "s");
        fixedTask.Start();
        fixedTask.Cancel();
        Assert.That(fixedTask.State, Is.EqualTo(TaskState.Running));
        Assert.That(fixedTask.Token.IsCancellationRequested, Is.False);

        var task = new TrackedTask("stoppable", "s", canCancel: true);
        task.Start();
        task.Cancel();
        Assert.That(task.Token.IsCancellationRequested, Is.True);
        Assert.That(task.Message, Is.EqualTo("Cancelling..."));
        Assert.That(task.State,   Is.EqualTo(TaskState.Running), "it ends as cancelled when the work notices");

        task.MarkCancelled();
        Assert.That(task.State, Is.EqualTo(TaskState.Cancelled));

        var queued = new TrackedTask("never started", "s", canCancel: true);
        queued.Cancel();
        Assert.That(queued.State, Is.EqualTo(TaskState.Cancelled), "a task that never started has no work to notice");
    }

    [Test]
    public void Cancelling_tells_the_work_before_it_notices()
    {
        var task = new TrackedTask("t", "s", canCancel: true);
        task.Start();
        var told = false;
        task.CancelRequested += () => told = true;

        task.Cancel();

        Assert.That(told, Is.True);
    }

    // ---- the center ------------------------------------------------------------------------------

    [Test]
    public async Task Run_tracks_work_through_success_and_keeps_what_it_reported()
    {
        var result = await TaskCenter.Run("Importing 2 files", "Silo import", async t =>
                                                                              {
                                                                                  await Task.Yield();
                                                                                  t.Report("1 of 2", 1);
                                                                                  t.SetResult(2, 4096);
                                                                                  return 42;
                                                                              });

        var task = TaskCenter.Tasks.Single();
        Assert.That(result, Is.EqualTo(42));
        Assert.That((task.Name, task.Source, task.State), Is.EqualTo(("Importing 2 files", "Silo import", TaskState.Succeeded)));
        Assert.That((task.ResultCount, task.ResultBytes), Is.EqualTo((2, 4096L)));
    }

    [Test]
    public async Task Run_records_a_failure_and_throws_it_on()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskCenter.Run("Exporting", "Table", _ => throw new InvalidOperationException("no space")));

        Assert.That(ex!.Message, Is.EqualTo("no space"));
        var task = TaskCenter.Tasks.Single();
        Assert.That((task.State, task.Message), Is.EqualTo((TaskState.Failed, "no space")));
    }

    [Test]
    public async Task Run_records_a_cancellation_when_the_work_stops_for_it()
    {
        ITrackedTask? seen = null;
        var run = TaskCenter.Run("Syncing", "Sync", async t =>
                                                    {
                                                        seen = t;
                                                        await Task.Delay(Timeout.Infinite, t.Token);
                                                    }, canCancel: true);

        SpinWait.SpinUntil(() => seen is not null, 2000);
        Assert.That(seen!.CanCancel, Is.True);
        seen.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(() => run);
        Assert.That(seen.State, Is.EqualTo(TaskState.Cancelled));
    }

    [Test]
    public async Task The_first_task_queued_shows_the_pane_once()
    {
        await TaskCenter.Run("one", "s", _ => Task.CompletedTask);
        await TaskCenter.Run("two", "s", _ => Task.CompletedTask);

        Assert.That(_reveals, Is.EqualTo(1));

        TaskCenter.Reset();
        TaskCenter.Reveal = () => _reveals++;
        await TaskCenter.Run("again", "s", _ => Task.CompletedTask);
        Assert.That(_reveals, Is.EqualTo(2), "a new session starts over");
    }

    [Test]
    public void Queueing_a_task_twice_lists_it_once_and_anything_that_implements_the_contract_can_be_queued()
    {
        var own = new OwnTask();

        TaskCenter.Queue(own);
        TaskCenter.Queue(own);

        Assert.That(TaskCenter.Tasks, Is.EqualTo(new[] { own }));
        Assert.That(TaskCenter.ActiveCount, Is.EqualTo(1));

        own.Finish();
        Assert.That(TaskCenter.ActiveCount, Is.EqualTo(0));
    }

    /// <summary>A task written without <see cref="TrackedTask" />: the contract is all the pane needs.</summary>
    private sealed class OwnTask : ITrackedTask
    {
        private TaskState _state = TaskState.Running;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string    Name               => "Custom work";
        public string    Source             => "Test";
        public TaskState State              => _state;
        public string?   Message            => null;
        public DateTime  QueuedUtc          { get; } = DateTime.UtcNow;
        public DateTime? StartedUtc         => QueuedUtc;
        public DateTime? EndedUtc           => null;
        public TimeSpan  Duration           => TimeSpan.Zero;
        public int?      ResultCount        => null;
        public long?     ResultBytes        => null;
        public bool      CanCancel          => false;
        public bool      NotifyOnCompletion { get; set; }

        public void Cancel() { }

        public void Finish()
        {
            _state = TaskState.Succeeded;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State)));
        }
    }

    [Test]
    public void The_toast_is_for_tasks_the_person_subscribed_to_and_only_when_they_end()
    {
        var watched = TaskCenter.Begin("watched", "s");
        var other   = TaskCenter.Begin("other", "s");
        watched.NotifyOnCompletion = true;

        Assert.That(_toasts, Is.Empty);
        other.Succeed();
        Assert.That(_toasts, Is.Empty, "not subscribed");

        watched.Succeed();
        Assert.That(_toasts, Is.EqualTo(new[] { watched }));

        // subscribing to a task that has ended announces nothing
        var late = TaskCenter.Begin("late", "s");
        late.Succeed();
        late.NotifyOnCompletion = true;
        Assert.That(_toasts, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_failed_or_cancelled_task_is_announced_too_and_only_once()
    {
        var failed    = TaskCenter.Begin("f", "s");
        var cancelled = TaskCenter.Begin("c", "s", canCancel: true);
        failed.NotifyOnCompletion = cancelled.NotifyOnCompletion = true;

        failed.Fail("boom");
        failed.Fail("again");
        cancelled.Cancel();
        cancelled.MarkCancelled();

        Assert.That(_toasts.Select(t => t.State), Is.EquivalentTo(new[] { TaskState.Failed, TaskState.Cancelled }));
    }

    [Test]
    public void The_toast_says_what_ended_how_with_its_count_size_and_time()
    {
        var task = new TrackedTask("Saving Plans.silo", "Silo file");
        task.Start();
        task.Succeed(null, 1, 12_345);

        var (title, detail) = TaskToast.Describe(task);

        Assert.That(title,  Is.EqualTo("Saving Plans.silo completed"));
        Assert.That(detail, Does.Contain("1 result").And.Contain("12,345 bytes"));

        var failed = new TrackedTask("Importing", "s");
        failed.Fail("not a Silo file");
        var (failTitle, failDetail) = TaskToast.Describe(failed);
        Assert.That(failTitle,  Is.EqualTo("Importing failed"));
        Assert.That(failDetail, Does.StartWith("not a Silo file"));
    }

    [Test]
    public void Ended_tasks_are_cleared_and_only_the_most_recent_are_kept()
    {
        var running = TaskCenter.Begin("running", "s");
        for (int i = 0; i < TaskCenter.MaxFinished + 20; i++) TaskCenter.Begin("t" + i, "s").Succeed();

        Assert.That(TaskCenter.Tasks.Count(t => t.State == TaskState.Succeeded), Is.EqualTo(TaskCenter.MaxFinished));
        Assert.That(TaskCenter.Tasks, Does.Contain(running), "a running task is never dropped");

        TaskCenter.ClearFinished();
        Assert.That(TaskCenter.Tasks, Is.EqualTo(new[] { running }));
    }

    // ---- work that is usually over in a blink ----------------------------------------------------

    [Test]
    public async Task A_deferred_task_that_finishes_quickly_never_appears_but_a_slow_or_failed_one_does()
    {
        var quick = TaskCenter.BeginDeferred("quick", "Explorer", TimeSpan.FromMilliseconds(150));
        quick.Succeed();
        var slow = TaskCenter.BeginDeferred("slow", "Explorer", TimeSpan.FromMilliseconds(40));
        var bad  = TaskCenter.BeginDeferred("bad", "Explorer", TimeSpan.FromMilliseconds(150));
        bad.Fail("unreadable");

        await Task.Delay(400);
        slow.Succeed(null, 7);

        Assert.That(TaskCenter.Tasks.Select(t => t.Name), Is.EquivalentTo(new[] { "slow", "bad" }));
        Assert.That(TaskCenter.Tasks.Single(t => t.Name == "slow").ResultCount, Is.EqualTo(7));
    }

    // ---- file work -------------------------------------------------------------------------------

    [Test]
    public void File_work_records_the_size_of_what_it_wrote_and_how_many_went_in()
    {
        var path = Path.Combine(Path.GetTempPath(), "silo-task-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            FileTasks.Write("Exporting table as CSV", "Table", path, () => File.WriteAllText(path, "abcdef"), 3);

            var task = TaskCenter.Tasks.Single();
            Assert.That((task.State, task.ResultCount, task.ResultBytes), Is.EqualTo((TaskState.Succeeded, 3, 6L)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void A_write_that_fails_is_recorded_and_thrown_on()
    {
        Assert.Throws<IOException>(() => FileTasks.Write("Saving", "Text editor", "x", () => throw new IOException("locked")));

        var task = TaskCenter.Tasks.Single();
        Assert.That((task.State, task.Message), Is.EqualTo((TaskState.Failed, "locked")));
    }

    [Test]
    public void Reading_records_the_size_and_the_count_the_read_returns()
    {
        var path = Path.Combine(Path.GetTempPath(), "silo-task-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "12345");
        try
        {
            var lines = FileTasks.Read("Opening a.txt", "Text editor", path, () => new[] { "a", "b" }, r => r.Length);

            var task = TaskCenter.Tasks.Single();
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That((task.ResultCount, task.ResultBytes), Is.EqualTo((2, 5L)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void The_size_of_a_file_is_null_when_there_is_none_and_the_sum_for_several()
    {
        var a = Path.Combine(Path.GetTempPath(), "silo-task-a-" + Guid.NewGuid().ToString("N"));
        var b = Path.Combine(Path.GetTempPath(), "silo-task-b-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(a, "123");
        File.WriteAllText(b, "45678");
        try
        {
            Assert.That(FileTasks.SizeOf(a),                              Is.EqualTo(3));
            Assert.That(FileTasks.SizeOf(new[] { a, b, "no-such-file" }), Is.EqualTo(8));
            Assert.That(FileTasks.SizeOf("no-such-file"),                 Is.Null);
            Assert.That(FileTasks.SizeOf((string?)null),                  Is.Null);
            Assert.That(FileTasks.SizeOf("https://example.test/x.silo"),  Is.Null);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Test]
    public void The_durations_a_toast_and_the_pane_show_are_short_and_plain()
    {
        Assert.That(TaskToast.Elapsed(TimeSpan.FromMilliseconds(250)), Is.EqualTo("250 ms"));
        Assert.That(TaskToast.Elapsed(TimeSpan.FromSeconds(2.5)),      Is.EqualTo("2.5 s"));
        Assert.That(TaskToast.Elapsed(TimeSpan.FromSeconds(125)),      Is.EqualTo("2:05"));
        Assert.That(TaskToast.Elapsed(TimeSpan.FromSeconds(3725)),     Is.EqualTo("1:02:05"));
    }
}

public class TasksPaneTests
{
    [Test]
    [Apartment(ApartmentState.STA)]
    public void The_pane_builds_and_follows_the_center_as_tasks_come_and_go()
    {
        TaskCenter.Reset();
        TaskCenter.Reveal = () => { };
        var tool = new TasksTool();
        var view = (System.Windows.Controls.UserControl)tool.GetContent()!;
        Assert.That(view, Is.Not.Null);
        using var _ = (IDisposable)view; // let go of the center when the test is over

        var task = TaskCenter.Begin("Saving x", "Silo file", canCancel: true);
        task.Report("half way", 1, 10);
        task.Succeed();

        Assert.That(TaskCenter.Tasks, Has.Count.EqualTo(1));
        Assert.That(tool.Name,        Is.EqualTo("tasks"));
        Assert.That(tool.DisplayName, Is.EqualTo("Tasks"));
        TaskCenter.Reset();
    }
}
