using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DeployGuard.Api.Data;

public static class DatabaseStartup
{
    /// <summary>Número qualquer, fixo, que identifica a trava das migrations no PostgreSQL.</summary>
    private const long MigrationLockKey = 0x4465706C6F79; // "Deploy" em hexadecimal

    /// <summary>
    /// Aplica as migrations mesmo com várias instâncias subindo ao mesmo tempo.
    ///
    /// Sem cuidado, as duas instâncias leem "faltam migrations" e as duas tentam criar as mesmas
    /// colunas: a segunda quebra com "column already exists" (aconteceu no teste com 2 réplicas).
    /// A solução é uma trava consultiva (advisory lock) do PostgreSQL: a primeira instância pega
    /// a trava e migra; a segunda espera, depois pega a trava, vê que está tudo aplicado e segue.
    /// </summary>
    public static async Task MigrateAsync(DeployGuardDbContext db, CancellationToken ct = default)
    {
        // A trava vive dentro de um banco, então o banco precisa existir antes.
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(ct))
        {
            try
            {
                await creator.CreateAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                // outra instância criou primeiro
            }
        }

        // pg_advisory_lock é da sessão: vale enquanto esta conexão estiver aberta.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({MigrationLockKey})", ct);
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({MigrationLockKey})", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }
}
