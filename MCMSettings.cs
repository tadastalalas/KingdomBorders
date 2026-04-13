using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace KingdomBorders
{
    internal class MCMSettings : AttributeGlobalSettings<MCMSettings>
    {
        public override string Id
        { get { return "KingdomBordersSettings"; } }

        public override string DisplayName
        { get { return "Kingdom Borders"; } }

        public override string FolderName
        { get { return "KingdomBorders"; } }

        public override string FormatType
        { get { return "json2"; } }

        [SettingPropertyBool("Hide Borders on Water", Order = 0, RequireRestart = false, HintText = "Hides border lines over water surfaces (sea, lakes and rivers). When disabled, borders are still visible behind the water. [Default: enabled]")]
        [SettingPropertyGroup("Water Rendering", GroupOrder = 0)]
        public bool HideBordersOnWater { get; set; } = true;

        [SettingPropertyInteger("Fade Start Height", 0, 300, Order = 0, RequireRestart = false, HintText = "Camera height at which borders start to become visible. Lower values make borders appear when zoomed in closer. [Default: 40]")]
        [SettingPropertyGroup("Visibility", GroupOrder = 1)]
        public int FadeStartHeight { get; set; } = 40;

        [SettingPropertyInteger("Full Opacity Height", 50, 500, Order = 1, RequireRestart = false, HintText = "Camera height at which borders reach full opacity. [Default: 200]")]
        [SettingPropertyGroup("Visibility", GroupOrder = 1)]
        public int FullOpacityHeight { get; set; } = 200;

        [SettingPropertyFloatingInteger("Border Width", 0.5f, 5.0f, "#0.0", Order = 0, RequireRestart = false, HintText = "Width of each kingdom's border strip in world units. [Default: 1.05]")]
        [SettingPropertyGroup("Appearance", GroupOrder = 2)]
        public float BorderWidth { get; set; } = 1.05f;

        [SettingPropertyFloatingInteger("Border Gap", 0.0f, 2.0f, "#0.00", Order = 1, RequireRestart = false, HintText = "Gap between two adjacent kingdom border strips. [Default: 0.30]")]
        [SettingPropertyGroup("Appearance", GroupOrder = 2)]
        public float BorderGap { get; set; } = 0.30f;
    }
}