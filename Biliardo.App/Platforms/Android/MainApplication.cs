#pragma warning disable IDE0130 // Namespace non coerente con struttura cartelle (scelta intenzionale in MAUI)

using Android.App;
using Android.Runtime;

namespace Biliardo.App
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
