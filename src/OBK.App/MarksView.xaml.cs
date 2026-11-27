using System.Windows;
using System.Windows.Controls;

namespace OBK.App;

public partial class MarksView : UserControl
{
    public static readonly DependencyProperty ListWidthProperty = DependencyProperty.Register(
        nameof(ListWidth), typeof(GridLength), typeof(MarksView), new PropertyMetadata(new GridLength(360)));
    public GridLength ListWidth
    {
        get => (GridLength)GetValue(ListWidthProperty);
        set => SetValue(ListWidthProperty, value);
    }
    public MarksView() => InitializeComponent();
}
