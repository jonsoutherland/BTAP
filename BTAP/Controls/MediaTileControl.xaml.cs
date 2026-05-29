using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using BTAP.Pages;

namespace BTAP.Controls;

public sealed partial class MediaTileControl : UserControl
{
    public const string DragDataFormat = "btap/media-id";

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(MediaTileData), typeof(MediaTileControl),
            new PropertyMetadata(null, OnDataChanged));

    public MediaTileData? Data
    {
        get => (MediaTileData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public MediaTileControl() => InitializeComponent();

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaTileControl ctrl && e.NewValue is MediaTileData data)
            ctrl.Refresh(data);
    }

    private void Refresh(MediaTileData data)
    {
        TypeIconText.Text = data.TypeIcon;
        NameText.Text = data.Name;
        DurationText.Text = data.Duration;
        TypeText.Text = data.TypeLabel;
    }

    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Data is null) return;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.Properties.Add(DragDataFormat, Data.Id);
        args.Data.SetText(Data.Id);
    }
}
