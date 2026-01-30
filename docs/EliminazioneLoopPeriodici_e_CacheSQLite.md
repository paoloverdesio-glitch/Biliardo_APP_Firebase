# Eliminazione loop periodici e cache SQLite

## Loop periodici eliminati
- **Pagina_MessaggiLista**: rimosso il loop periodico della lista chat (`Task.Delay` loop). Aggiornamento solo via push + SQLite e refresh esplicito utente.
- **Pagina_Home**: rimosso il loop periodico del feed (`PollHomeOnceAsync` e loop con `Task.Delay`).
- **Pagina_MessaggiDettaglio**: rimosso il loop chat e `LoadOnceFromServerAsync` automatico; nessun fetch su `OnAppearing`.
- **ForegroundDeliveredReceiptsService**: servizio eliminato (loop in foreground con `Task.Delay`).

## Nuovi flussi push-only
- **Chat**
  - Push FCM → `PushCacheUpdater` aggiorna SQLite (Messages + Chats) usando solo il payload.
  - UI aggiornata via `BusEventiRealtime` senza fetch automatico.
  - Se payload non contiene dettagli sufficienti: placeholder **“Contenuto disponibile”** con pulsante **“Sincronizza”** (fetch esplicito).
  - Delivered: inviato su ricezione push (se non mittente). Read: inviato quando il messaggio entra nel viewport, con throttling 350ms.
- **Home feed**
  - Push FCM → `PushCacheUpdater` aggiorna SQLite (HomeFeed) usando solo il payload.
  - UI aggiornata via `BusEventiRealtime` senza fetch automatico.
  - Se payload è incompleto: placeholder **“Contenuto disponibile”** + pulsante **“Sincronizza”**.

## Cache SQLite (FLASH + RAM)
### Tabelle obbligatorie + indici
- **Chats**: `ChatId` (PK), `PeerUid`, `LastMessageId`, `UnreadCount`, `UpdatedAtUtc`.
- **Messages**: `ChatId` + `MessageId` (PK), `SenderId`, `Text`, `MediaKey`, `CreatedAtUtc`.
- **HomeFeed**: `PostId` (PK), `AuthorName`, `Text`, `ThumbKey`, `CreatedAtUtc`.
- **MediaCache**: `CacheKey` (PK), `Sha256` (UNIQUE), `Kind`, `LocalPath`, `SizeBytes`, `LastAccessUtc`.
- **MediaAliases**: `AliasKey` (PK) → `CacheKey`.
- **Profiles** (supporto cache profili): `Uid` (PK), `Nickname`, `FirstName`, `LastName`, `PhotoUrl`, `UpdatedAtUtc`.
- **Indici**: `IX_Messages_Chat_CreatedAtUtc`, `IX_HomeFeed_CreatedAtUtc`, `IX_MediaCache_LastAccessUtc`, `IX_Chats_UpdatedAtUtc`.

### Regole di lettura (RAM)
- **Chat aperta**: caricamento immediato da SQLite con query **`ORDER BY CreatedAtUtc DESC LIMIT 30`** e render in RAM.
- **Lazy load**: scroll verso l’alto → altri 30 messaggi da SQLite con la stessa regola di ordinamento/limit.
- **Home**: apertura con ultimi 30 post da SQLite (`ORDER BY CreatedAtUtc DESC LIMIT 30`).

## Cache media (1GB + LRU + deduplicazione)
- Archiviazione su `FileSystem.AppDataDirectory`.
- Deduplicazione tramite `Sha256` (UNIQUE) con alias in `MediaAliases`.
- Eviction LRU su `MediaCache.LastAccessUtc` al superamento di 1GB.
- Upload: file registrato in cache **prima** dell’invio; al termine, alias `storagePath → cacheKey`.
- Download: se presente in cache (anche via alias), **mai riscaricare**.

## Build (Android + Windows)
- `dotnet build /workspace/Biliardo_APP_Firebase/Biliardo.App/Biliardo.App.csproj -f net8.0-android` → **fallito** (`dotnet` non disponibile nell’ambiente).
- `dotnet build /workspace/Biliardo_APP_Firebase/Biliardo.App/Biliardo.App.csproj -f net8.0-windows10.0.19041.0` → **fallito** (`dotnet` non disponibile nell’ambiente).
