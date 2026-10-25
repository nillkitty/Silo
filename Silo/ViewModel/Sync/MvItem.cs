using Silo.DbModel.Meta;

namespace Silo.Model.Metaverse;

public class MvItem : ModelBase<MvObj>
{
    public MvItem() : base(App.LocalConnection)
    {
    }
}