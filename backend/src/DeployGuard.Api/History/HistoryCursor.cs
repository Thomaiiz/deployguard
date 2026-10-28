using System.Buffers.Text;
using System.Text;

namespace DeployGuard.Api.History;

/// <summary>
/// Cursor de paginação "keyset": em vez de OFFSET (que fica lento e pula/duplica linhas
/// quando dados novos chegam), a próxima página começa logo depois do último item visto.
/// O cursor é opaco para o cliente: (CheckedAt, Id) codificados em Base64 URL-safe.
/// </summary>
public readonly record struct HistoryCursor(DateTimeOffset CheckedAt, long Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CheckedAt.UtcTicks}:{Id}"));

    public static bool TryDecode(string? value, out HistoryCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && long.TryParse(parts[1], out var id)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks)
            {
                cursor = new HistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
