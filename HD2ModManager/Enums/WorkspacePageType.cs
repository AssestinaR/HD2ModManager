namespace HD2ModManager.Enums
{
    // 作用：描述主工作区左右 slot 中可承载的页面类型。
    public enum WorkspacePageType
    {
        Home,
        Profile,
        Library,
        Settings,
        ModDetails,
        AdvancedModDetails,
        GameDataBrowser,
        GameDataArchiveDetails,
        CrossArmorPlan,
        MaterialPackaging,
        PatchResourceRepackaging,
        HelmetToArmorMergePlan,
        ArmorRegistry,
        // 分件模组（sectional）的 section 组 / 树编辑页。
        // 与详情页分开：那是"这个 Mod 是什么"，这是"它有哪些可开关的块"。
        SectionalEditor,
    }
}
