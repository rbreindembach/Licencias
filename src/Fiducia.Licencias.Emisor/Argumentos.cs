namespace Fiducia.Licencias.Emisor;

/// <summary>
/// Linea de comandos ya separada: el comando, las opciones (<c>--nombre valor</c>, repetibles) y los argumentos sueltos.
/// Sin dependencias: son cuatro comandos con pocas opciones.
/// </summary>
internal sealed class Argumentos
{
    private readonly Dictionary<string, List<string>> _opciones = new(StringComparer.OrdinalIgnoreCase);

    public string? Comando { get; private init; }

    public List<string> Sueltos { get; } = [];

    /// <summary>Separa los argumentos, o devuelve el error (una opcion sin valor).</summary>
    public static (Argumentos? Argumentos, string? Error) Leer(IReadOnlyList<string> args)
    {
        var resultado = new Argumentos { Comando = args.Count > 0 ? args[0].ToLowerInvariant() : null };
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                resultado.Sueltos.Add(arg);
                continue;
            }

            var nombre = arg[2..];
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return (null, $"Falta el valor de --{nombre}.");
            }

            if (!resultado._opciones.TryGetValue(nombre, out var valores))
            {
                resultado._opciones[nombre] = valores = [];
            }

            valores.Add(args[++i]);
        }

        return (resultado, null);
    }

    public string? Opcion(string nombre) => _opciones.TryGetValue(nombre, out var v) ? v[^1] : null;

    public IReadOnlyList<string> Opciones(string nombre) => _opciones.TryGetValue(nombre, out var v) ? v : [];

    public IEnumerable<string> Nombres => _opciones.Keys;
}
