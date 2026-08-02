using Microsoft.EntityFrameworkCore;

namespace Ppki.Infrastructure;

public static class DatabaseInitializer
{
    public static async Task VerifyAndSeedRulesAsync(PpkiDbContext db, string ruleCatalogPath, CancellationToken cancellationToken = default)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Cannot connect to Supabase Postgres. Check SUPABASE_DB_CONNECTION.");
        try { _ = await db.DocumentTypes.CountAsync(cancellationToken); }
        catch (Exception ex) { throw new InvalidOperationException("Supabase schema is missing. Run `npx supabase db push` before starting the API.", ex); }
        await RuleCatalogImporter.ImportAsync(db, ruleCatalogPath, cancellationToken);
    }
}
