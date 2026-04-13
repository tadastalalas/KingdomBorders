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

        [SettingPropertyInteger("Fade Start Height", 0, 300, Order = 0, RequireRestart = false, HintText = "Camera height at which borders start to become visible. Lower values make borders appear when zoomed in closer. [Default: 60]")]
        [SettingPropertyGroup("Visibility", GroupOrder = 0)]
        public int FadeStartHeight { get; set; } = 60;

        [SettingPropertyInteger("Full Opacity Height", 50, 500, Order = 1, RequireRestart = false, HintText = "Camera height at which borders reach full opacity. [Default: 400]")]
        [SettingPropertyGroup("Visibility", GroupOrder = 0)]
        public int FullOpacityHeight { get; set; } = 400;

        [SettingPropertyFloatingInteger("Border Width", 0.5f, 5.0f, "#0.0", Order = 0, RequireRestart = false, HintText = "Width of each kingdom's border strip in world units. [Default: 1.05]")]
        [SettingPropertyGroup("Appearance", GroupOrder = 1)]
        public float BorderWidth { get; set; } = 1.05f;

        [SettingPropertyFloatingInteger("Border Gap", 0.0f, 2.0f, "#0.00", Order = 1, RequireRestart = false, HintText = "Gap between two adjacent kingdom border strips. [Default: 0.30]")]
        [SettingPropertyGroup("Appearance", GroupOrder = 1)]
        public float BorderGap { get; set; } = 0.30f;

        [SettingPropertyBool("Show Borders on Water", Order = 0, RequireRestart = false, HintText = "Renders borders over water surfaces. This causes water surface transparent. [Default: disabled]")]
        [SettingPropertyGroup("Experimental", GroupOrder = 99)]
        public bool ShowBordersOnWater { get; set; } = false;
    }
}