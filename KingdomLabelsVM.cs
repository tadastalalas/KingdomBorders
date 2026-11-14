using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace KingdomBorders
{
    public class KingdomLabelVM : ViewModel
    {
        internal Kingdom Kingdom;
        internal Vec3 WorldPos;

        private string _name = "";
        private string _info = "";
        private float _screenX;
        private float _screenY;
        private float _alpha;
        private bool _isVisible;

        [DataSourceProperty]
        public string Name { get => _name; set { if (value != _name) { _name = value; OnPropertyChangedWithValue(value, nameof(Name)); } } }

        [DataSourceProperty]
        public string Info { get => _info; set { if (value != _info) { _info = value; OnPropertyChangedWithValue(value, nameof(Info)); } } }

        [DataSourceProperty]
        public float ScreenX { get => _screenX; set { if (value != _screenX) { _screenX = value; OnPropertyChangedWithValue(value, nameof(ScreenX)); } } }

        [DataSourceProperty]
        public float ScreenY { get => _screenY; set { if (value != _screenY) { _screenY = value; OnPropertyChangedWithValue(value, nameof(ScreenY)); } } }

        [DataSourceProperty]
        public float Alpha { get => _alpha; set { if (value != _alpha) { _alpha = value; OnPropertyChangedWithValue(value, nameof(Alpha)); } } }

        [DataSourceProperty]
        public bool IsVisible { get => _isVisible; set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } } }
    }

    public class KingdomLabelsVM : ViewModel
    {
        private MBBindingList<KingdomLabelVM> _labels = [];

        [DataSourceProperty]
        public MBBindingList<KingdomLabelVM> Labels { get => _labels; set { if (value != _labels) { _labels = value; OnPropertyChangedWithValue(value, nameof(Labels)); } } }
    }
}