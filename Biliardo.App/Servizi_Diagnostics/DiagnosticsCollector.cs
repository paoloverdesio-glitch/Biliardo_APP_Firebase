using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Diagnostics
{
    public static class DiagnosticsCollector
    {
        // Wrapper unificato: usa il servizio nuovo
        public static Task<bool> SendNowAsync(string contextLabel = "")
            => DiagMailService.SendNowAsync(contextLabel);
    }
}
