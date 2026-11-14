using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using MCM.Common;
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

        [SettingPropertyText("{=KB_e2dqLpQUOt}Toggle Borders Hotkey", Order = 2, RequireRestart = false, HintText = "{=KB_sgyNhjlxl4}Key to cycle the display: off, borders, borders + territory tint, territory tint only. With Territory Tint disabled it only turns borders on or off. [Default: M]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public string ToggleBordersHotkey { get; set; } = "M";

        [SettingPropertyBool("{=KB_OnlyPlayer}Only Player Kingdom Borders", Order = 3, RequireRestart = false, HintText = "{=KB_OnlyPlayerHint}Draw only borders your kingdom shares with its neighbours. Ignored if you have no kingdom. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public bool OnlyPlayerBorders { get; set; } = false;

        [SettingPropertyBool("{=KB_Tint}Territory Tint", Order = 4, RequireRestart = false, HintText = "{=KB_TintHint}Low-res kingdom colour fill, visible when zoomed far out (cycle with the hotkey). [Default: enabled]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public bool ShowTerritoryTint { get; set; } = true;

        [SettingPropertyFloatingInteger("{=KB_TintOpacity}Territory Tint Opacity", 0.05f, 0.5f, "#0.00", Order = 5, RequireRestart = false, HintText = "{=KB_TintOpacityHint}[Default: 0.15]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public float TintOpacity { get; set; } = 0.15f;
        
        [SettingPropertyBool("{=KB_Labels}Kingdom Name Labels", Order = 6, RequireRestart = false, HintText = "{=KB_LabelsHint}Shows each kingdom's name and fief/clan count over its territory when zoomed out. [Default: enabled]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public bool ShowKingdomLabels { get; set; } = true;
        
        [SettingPropertyInteger("{=KB_LabelHeight}Label Height", 20, 300, Order = 7, RequireRestart = false, HintText = "{=KB_LabelHeightHint}Camera height above which kingdom labels are shown. With Debug Logging on, the hotkey message shows the current camera height. [Default: 300]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public int LabelHeight { get; set; } = 300;
        
        [SettingPropertyInteger("{=KB_TownPlateHeight}Hide Town Nameplates Above", 0, 800, Order = 8, RequireRestart = false, HintText = "{=KB_TownPlateHeightHint}Camera height above which town nameplates are hidden, like vanilla hides village/castle plates. 0 = never. [Default: 600]")]
        [SettingPropertyGroup("{=KB_bE7uoWvvhj}Visibility", GroupOrder = 0)]
        public int TownNameplateHideHeight { get; set; } = 600;

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
        
        [SettingPropertyDropdown("{=KB_Style}Border Style", Order = 4, RequireRestart = false, HintText = "{=KB_StyleHint}Solid, dashed or dotted strips. [Default: Solid]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public Dropdown<string> BorderStyle { get; set; } = new Dropdown<string>(["Solid", "Dashed", "Dotted"], 0);

        [SettingPropertyBool("{=KB_Emph}Emphasize Player Kingdom", Order = 5, RequireRestart = false, HintText = "{=KB_EmphHint}Your kingdom's strips are wider and brighter; others are dimmed. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public bool EmphasizePlayerKingdom { get; set; } = false;

        [SettingPropertyBool("{=KB_War}Pulse Borders At War", Order = 6, RequireRestart = false, HintText = "{=KB_WarHint}Strips between kingdoms at war pulse. [Default: enabled]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public bool HighlightWarBorders { get; set; } = true;

        [SettingPropertyBool("{=KB_Glow}Glow Recently Changed Borders", Order = 7, RequireRestart = false, HintText = "{=KB_GlowHint}Newly gained border glows for one day. [Default: enabled]")]
        [SettingPropertyGroup("{=KB_ILCyBhgBj8}Appearance", GroupOrder = 1)]
        public bool HighlightRecentChanges { get; set; } = true;

        [SettingPropertyInteger("{=KB_GG5BW78w88}Grid Resolution", 50, 300, Order = 0, RequireRestart = false, HintText = "{=KB_r5mBKIWelk}Resolution of the territory grid used to calculate borders. Higher values produce more precise borders at the cost of rebuild performance. [Default: 150]")]
        [SettingPropertyGroup("{=KB_99NOgT2Y0n}Performance", GroupOrder = 2)]
        public int GridResolution { get; set; } = 150;

        [SettingPropertyBool("{=KB_Y0GmZgp0H3}Enable Debug Logging", Order = 1, RequireRestart = false, HintText = "{=KB_mvcbyndDid}Writes detailed diagnostic information to KingdomBorders.log in the logs folder inside the mod folder. Disable to avoid unnecessary disk writes. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_99NOgT2Y0n}Performance", GroupOrder = 2)]
        public bool EnableDebugLogging { get; set; } = false;

        [SettingPropertyBool("{=KB_dd8z7WavCY}Show Borders on Water", Order = 0, RequireRestart = false, HintText = "{=KB_rKUsLbE1lp}Renders borders over water surfaces. [Default: disabled]")]
        [SettingPropertyGroup("{=KB_ijWOSHr4JT}Experimental", GroupOrder = 3)]
        public bool ShowBordersOnWater { get; set; } = false;
    }
}