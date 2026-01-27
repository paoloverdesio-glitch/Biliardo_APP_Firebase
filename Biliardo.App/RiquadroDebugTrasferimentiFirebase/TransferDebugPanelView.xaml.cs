using Microsoft.Maui.Controls;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public partial class TransferDebugPanelView : ContentView
    {
        public TransferDebugPanelView()
        {
            InitializeComponent();
            BindingContext = FirebaseTransferDebugMonitor.Instance;
        }
    }
}
