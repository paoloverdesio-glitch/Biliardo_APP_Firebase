namespace Biliardo.App.Pagine_Home;

// Parametri globali Home feed.
public static class PaginaHomeSettings
{
    // Se true ignora max_post_in_ram (lista senza trimming).
    public static bool post_illimitati = true;

    // Se post_illimitati è false, i post in eccesso vengono rimossi dal fondo quando la lista è idle.
    public static int max_post_in_ram = 500;

    // Trigger prefetch post vecchi (percentuale della lista già superata in scroll).
    public static int prefetch_trigger_percent = 50;

    // Dimensione pagina Firestore.
    public static int page_size = 20;

    // Limite snapshot cache RAM serializzato per evitare freeze su liste enormi.
    public static int memory_cache_snapshot_limit = 300;
}
