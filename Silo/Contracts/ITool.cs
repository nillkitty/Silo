using System.Windows;
using System.Windows.Media;
using Telerik.Windows.Controls;

namespace Silo.Contracts;

/// <summary>
///     A "Tool" registered with the app which appears in the Tools menu.
/// </summary>
public interface ITool
{
    /// <summary>
    ///     User configurable priority (order)
    /// </summary>
    string? DisplayName { get; set; }

    /// <summary>
    ///     Display text for the tool's Tools-menu entry: <see cref="DisplayName"/> if
    ///     the user has set one, otherwise the tool's <see cref="Name"/>. This is a
    ///     default interface member for convenience when working with an
    ///     <see cref="ITool"/> reference directly; because default interface members
    ///     aren't visible to WPF's reflection-based binding, <c>ToolBase</c> also
    ///     declares a concrete <c>Header</c> property so menu bindings resolve it too.
    /// </summary>
    string Header => DisplayName ?? Name;

    /// <summary>
    ///     Optional string used to group menu items into clusters of similar
    ///     commands, separated by menu separators.
    /// </summary>
    string? GroupName { get; }

    /// <summary>
    ///     Gets the source of an optional icon.
    /// </summary>
    ImageSource? Icon { get; }

    /// <summary>
    ///     Gets whether the tool's window is currently open.  Drives the checkmark
    ///     next to the tool's entry in the Tools menu.
    /// </summary>
    bool IsChecked { get; }

    /// <summary>
    ///     Gets whether the tool is executable.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    ///     Gets whether the tool is visible in the tool menu
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    ///     Gets a unique key value used to track the tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     User editable display name
    /// </summary>
    int Priority { get; set; }

    /// <summary>
    ///     Executes the tool: opens its window if it is currently closed, or closes
    ///     (hides) it if it is currently open.  This is the single entry point invoked
    ///     when the tool's Tools-menu item is clicked, so that a checkable menu item's
    ///     <see cref="IsChecked"/> state and its click behavior never disagree - unlike
    ///     the old "ShowWindow" verb, which only ever showed the window and left callers
    ///     to invent their own (inconsistent) way of hiding it again.
    /// </summary>
    /// <returns>The tool's window if it is now open, or <see langword="null"/> if it was just closed.</returns>
    Window? Toggle();

    /// <summary>
    ///     Gets the tool control (but does not put it in a Window, Pane, or otherwise prepare it)
    /// </summary>
    /// <returns></returns>
    UIElement? GetContent();

    /// <summary>
    ///     Gets a RadPane contaiming the tool
    /// </summary>
    /// <returns></returns>
    RadPane? GetPane();
}