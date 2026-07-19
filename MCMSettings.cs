using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using TaleWorlds.Localization;

namespace KingdomBorders
{
    internal class MCMSettings : AttributeGlobalSettings<MCMSettings>
    {
        public override string Id
        { get { return "KingdomBordersSettings"; } }

        public override string DisplayName
        { get { return new TextObject("{=KB_EBzIuYethH}Kingdom Borders").ToString(); } }

        public override string FolderName
        { get { return "KingdomBorders"; } }

        public override string FormatType
        { get { return "json2"; } }

        [SettingPropertyInteger("{=KB_SSoTUbKffp}Fade Start Height", 0, 300, Order = 0, RequireRestart = false, HintText = "{=KB_tSWvnFByJ5}Camera height at which borders start to become visible. Lower values make borders appear when zoomed in closer. [Default: 30]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public int FadeStartHeight { get; set; } = 30;

        [SettingPropertyInteger("{=KB_LLay9BBZaY}Full Opacity Height", 50, 500, Order = 1, RequireRestart = false, HintText = "{=KB_Nvt3nDz9B9}Camera height at which borders reach full opacity. [Default: 300]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public int FullOpacityHeight { get; set; } = 300;

        [SettingPropertyText("{=KB_e2dqLpQUOt}Toggle Borders Hotkey", Order = 2, RequireRestart = false, HintText = "{=KB_sgyNhjlxl4}Key to toggle border visibility on/off. [Default: M]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public string ToggleBordersHotkey { get; set; } = "M";

        [SettingPropertyFloatingInteger("{=KB_j3dQf5mdFj}Border Width", 0.5f, 5.0f, "#0.0", Order = 0, RequireRestart = false, HintText = "{=KB_rZKzD08uWg}Width of each kingdom's border strip in world units. [Default: 1.05]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public float BorderWidth { get; set; } = 1.05f;

        [SettingPropertyFloatingInteger("{=KB_44YGFjBRwD}Border Gap", 0.0f, 2.0f, "#0.00", Order = 1, RequireRestart = false, HintText = "{=KB_esaK0vgYFX}Gap between two adjacent kingdom border strips. [Default: 0.30]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public float BorderGap { get; set; } = 0.30f;

        [SettingPropertyFloatingInteger("{=KB_iQDKtjjGgA}Height Offset", 0.1f, 3.0f, "#0.00", Order = 2, RequireRestart = false, HintText = "{=KB_r5gC0YQqcu}How far above the terrain surface the border is raised. Increase if borders clip into mountains. [Default: 0.55]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public float HeightOffset { get; set; } = 0.55f;

        [SettingPropertyInteger("{=KB_Xsi6Fl5Zp6}Corner Smoothing", 0, 6, Order = 3, RequireRestart = false, HintText = "{=KB_GEvl58fFtW}Number of smoothing iterations applied to border corners. Higher values produce smoother curves but increase geometry and rebuild cost. [Default: 3]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public int CornerSmoothing { get; set; } = 3;

        [SettingPropertyInteger("{=KB_GG5BW78w88}Grid Resolution", 50, 300, Order = 0, RequireRestart = false, HintText = "{=KB_r5mBKIWelk}Resolution of the territory grid used to calculate borders. Higher values produce more precise borders at the cost of rebuild performance. [Default: 150]")]
        [SettingPropertyGroup("{=KB_99NOgT2Y0n}Performance", GroupOrder = 2)]
        public int GridResolution { get; set; } = 150;

        [SettingPropertyBool("{=KB_Y0GmZgp0H3}Enable Debug Logging", Order = 1, RequireRestart = false, HintText = "{=KB_mvcbyndDid}Writes detailed diagnostic information to KingdomBorders.log in the Configs folder. Disable to avoid unnecessary disk writes. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_99NOgT2Y0n}Performance", GroupOrder = 2)]
        public bool EnableDebugLogging { get; set; } = false;

        [SettingPropertyBool("{=KB_dd8z7WavCY}Show Borders on Water", Order = 0, RequireRestart = false, HintText = "{=KB_rKUsLbE1lp}Renders borders over water surfaces. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_ijWOSHr4JT}Experimental", GroupOrder = 3)]
        public bool ShowBordersOnWater { get; set; } = false;
    }
}