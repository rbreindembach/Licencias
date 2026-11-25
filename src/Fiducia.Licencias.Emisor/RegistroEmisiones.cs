using System.Globalization;
using System.Text;
using Fiducia.BuildingBlocks.Licencias;

namespace Fiducia.Licencias.Emisor;

/// <summary>
/// Registro de las licencias emitidas (<c>registro.csv</c> en la carpeta de trabajo, fuera del repo): numera los Id
/// (LIC-AAAA-NNNN por año de inicio de vigencia) y evita repetir uno, que el sistema rechazaria al cargarlo.
/// </summary>
internal sealed class RegistroEmisiones(string archivo, string prefijoId = "LIC")
{
    private const string Encabezado = "Id;Cliente;Rif;Dominios;Desde;Hasta;Ambiente;MesesGracia;Emitida;Archivo";

    public IReadOnlyList<string> Ids() =>
        File.Exists(archivo)
            ? File.ReadAllLines(archivo, Encoding.UTF8).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')[0]).ToList()
            : [];

    public bool Existe(string id) => Ids().Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>El siguiente Id libre para el año: LIC-2027-0001, LIC-2027-0002... (CAJA-2027-0001 en Caja 2000).</summary>
    public string SiguienteId(int anio)
    {
        var prefijo = $"{prefijoId}-{anio}-";
        var ultimo = Ids()
            .Where(id => id.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase))
            .Select(id => int.TryParse(id[prefijo.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefijo}{ultimo + 1:D4}";
    }

    public void Agregar(Licencia licencia, string archivoLicencia)
    {
        var nuevo = !File.Exists(archivo);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivo))!);
        using var escritor = new StreamWriter(archivo, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (nuevo)
        {
            escritor.WriteLine(Encabezado);
        }

        escritor.WriteLine(string.Join(';',
            licencia.Id, Limpiar(licencia.Cliente), Limpiar(licencia.Rif), string.Join(' ', licencia.Dominios),
            licencia.VigenteDesde.ToString("yyyy-MM-dd"), licencia.VigenteHasta.ToString("yyyy-MM-dd"), licencia.Ambiente,
            licencia.MesesGracia, licencia.Emitida.ToString("yyyy-MM-dd"), archivoLicencia));
    }

    private static string Limpiar(string texto) => texto.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
}
