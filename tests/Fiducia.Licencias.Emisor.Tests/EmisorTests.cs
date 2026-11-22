using Fiducia.BuildingBlocks.Licencias;

namespace Fiducia.Licencias.Emisor.Tests;

/// <summary>
/// Cada prueba trabaja en una carpeta temporal propia con claves efimeras; las licencias emitidas se verifican con
/// <see cref="ArchivoLicencia"/> de Fiducia, la misma biblioteca que las acepta en el sistema.
/// </summary>
public sealed class EmisorTests : IDisposable
{
    private static readonly DateOnly Hoy = new(2027, 4, 10);
    private readonly string _carpeta = Directory.CreateTempSubdirectory("licencias-pruebas-").FullName;
    private readonly StringWriter _salida = new();

    public void Dispose() => Directory.Delete(_carpeta, recursive: true);

    private int Correr(params string[] args) =>
        new Emisor(new Entorno(_carpeta, _ => null, Hoy), _salida).Ejecutar(args);

    private string Ruta(string nombre) => Path.Combine(_carpeta, nombre);

    /// <summary>Genera las claves "prueba" y devuelve la ruta de la privada.</summary>
    private string ClavePrivada()
    {
        Assert.Equal(0, Correr("generar-claves", "--nombre", "prueba"));
        return Ruta("prueba-privada.pem");
    }

    private string[] Emision(string clave, params string[] extra) =>
    [
        "emitir", "--cliente", "Fiduciaria Uno, C.A.", "--rif", "J-11111111-1", "--dominio", "fiduciaapi.uno.com",
        "--desde", "2026-12-01", "--ambiente", "produccion", "--clave", clave, .. extra,
    ];

    private Licencia Leer(string archivo, string clavePublica) =>
        ArchivoLicencia.Leer(File.ReadAllText(archivo), File.ReadAllText(clavePublica)).Licencia
        ?? throw new Xunit.Sdk.XunitException("La licencia emitida no pasa la verificación de Fiducia.");

    // --- generar-claves ---

    [Fact]
    public void Generar_claves_crea_el_par_y_muestra_la_publica_y_su_huella()
    {
        // Arrange (carpeta vacia)

        // Act
        var codigo = Correr("generar-claves", "--nombre", "clave-2027");

        // Assert
        Assert.Equal(0, codigo);
        Assert.True(File.Exists(Ruta("clave-2027-privada.pem")));
        var publica = File.ReadAllText(Ruta("clave-2027-publica.pem"));
        Assert.Contains("BEGIN PUBLIC KEY", _salida.ToString());
        Assert.Contains(Claves.Huella(publica), _salida.ToString());
        Assert.DoesNotContain("PRIVATE KEY", _salida.ToString());
    }

    [Fact]
    public void Generar_claves_deja_la_privada_legible_solo_por_el_duenio()
    {
        // Arrange
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Act
        Correr("generar-claves", "--nombre", "prueba");

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Ruta("prueba-privada.pem")));
    }

    [Fact]
    public void Generar_claves_no_sobrescribe_una_clave_existente()
    {
        // Arrange
        Correr("generar-claves", "--nombre", "prueba");
        var original = File.ReadAllText(Ruta("prueba-privada.pem"));

        // Act
        var codigo = Correr("generar-claves", "--nombre", "prueba");

        // Assert
        Assert.Equal(1, codigo);
        Assert.Equal(original, File.ReadAllText(Ruta("prueba-privada.pem")));
    }

    // --- emitir ---

    [Fact]
    public void Emitir_produce_una_licencia_que_Fiducia_acepta()
    {
        // Arrange
        var clave = ClavePrivada();

        // Act
        var codigo = Correr(Emision(clave, "--dominio", "FiduciaApi.Uno.com", "--dominio", "fiduciaapi-dr.uno.com"));

        // Assert
        Assert.Equal(0, codigo);
        var licencia = Leer(Ruta("emitidas/LIC-2026-0001.json"), Ruta("prueba-publica.pem"));
        Assert.Equal("Fiduciaria Uno, C.A.", licencia.Cliente);
        Assert.Equal(["fiduciaapi.uno.com", "fiduciaapi-dr.uno.com"], licencia.Dominios);   // en minusculas y sin repetir
        Assert.Equal(new DateOnly(2026, 12, 1), licencia.VigenteDesde);
        Assert.Equal(new DateOnly(2027, 11, 30), licencia.VigenteHasta);                  // un año por defecto
        Assert.Equal(3, licencia.MesesGracia);
        Assert.Equal(Hoy, licencia.Emitida);
        Assert.Equal(AmbienteLicencia.Produccion, licencia.Ambiente);
    }

    [Fact]
    public void Emitir_numera_por_anio_y_lo_anota_en_el_registro()
    {
        // Arrange
        var clave = ClavePrivada();

        // Act
        Correr(Emision(clave));
        Correr(Emision(clave, "--cliente", "Otra", "--desde", "2026-06-01"));
        Correr(Emision(clave, "--desde", "2027-01-15"));

        // Assert
        Assert.True(File.Exists(Ruta("emitidas/LIC-2026-0001.json")));
        Assert.True(File.Exists(Ruta("emitidas/LIC-2026-0002.json")));
        Assert.True(File.Exists(Ruta("emitidas/LIC-2027-0001.json")));
        var registro = File.ReadAllLines(Ruta("registro.csv"));
        Assert.Equal(4, registro.Length);   // encabezado + 3
        Assert.StartsWith("LIC-2026-0002;Otra;", registro[2]);
    }

    [Fact]
    public void Emitir_rechaza_un_id_ya_emitido()
    {
        // Arrange
        var clave = ClavePrivada();
        Correr(Emision(clave, "--id", "LIC-ESPECIAL-1"));

        // Act
        var codigo = Correr(Emision(clave, "--id", "LIC-ESPECIAL-1", "--salida", Ruta("otra.json")));

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("ya fue emitida", _salida.ToString());
        Assert.False(File.Exists(Ruta("otra.json")));
    }

    [Theory]
    [InlineData("--ambiente", "produccio", "--ambiente")]
    [InlineData("--desde", "01/12/2026", "--desde")]
    [InlineData("--gracia", "13", "--gracia")]
    [InlineData("--dominio", "https://fiduciaapi.uno.com", "--dominio")]
    [InlineData("--dominio", "fiduciaapi.uno.com:7100", "--dominio")]
    [InlineData("--hasta", "2026-11-30", "anterior a su inicio")]
    public void Emitir_rechaza_valores_invalidos_sin_crear_nada(string opcion, string valor, string mensaje)
    {
        // Arrange
        var clave = ClavePrivada();

        // Act
        var codigo = Correr(Emision(clave, opcion, valor));

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains(mensaje, _salida.ToString());
        Assert.False(Directory.Exists(Ruta("emitidas")));
    }

    [Fact]
    public void Una_opcion_mal_escrita_es_un_error_y_no_se_ignora()
    {
        // Arrange
        var clave = ClavePrivada();

        // Act: --garcia en lugar de --gracia no puede terminar emitiendo con la gracia por defecto
        var codigo = Correr(Emision(clave, "--garcia", "6"));

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("--garcia", _salida.ToString());
    }

    [Fact]
    public void Emitir_exige_las_opciones_obligatorias()
    {
        // Arrange
        var clave = ClavePrivada();

        // Act
        var codigo = Correr("emitir", "--cliente", "X", "--clave", clave);

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("--rif, --dominio, --desde, --ambiente", _salida.ToString());
    }

    [Fact]
    public void Emitir_sin_clave_explica_como_indicarla()
    {
        // Arrange (sin --clave ni variable de entorno)

        // Act
        var codigo = Correr("emitir", "--cliente", "X", "--rif", "J-1", "--dominio", "a.com", "--desde", "2026-12-01",
            "--ambiente", "pruebas");

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains(Entorno.VariableClave, _salida.ToString());
    }

    [Fact]
    public void Emitir_con_la_clave_publica_en_lugar_de_la_privada_es_un_error()
    {
        // Arrange
        ClavePrivada();

        // Act
        var codigo = Correr(Emision(Ruta("prueba-publica.pem")));

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("privada", _salida.ToString());
    }

    [Fact]
    public void La_clave_se_puede_indicar_por_variable_de_entorno()
    {
        // Arrange
        var clave = ClavePrivada();
        var emisor = new Emisor(new Entorno(_carpeta, v => v == Entorno.VariableClave ? clave : null, Hoy), _salida);

        // Act
        var codigo = emisor.Ejecutar(["emitir", "--cliente", "X", "--rif", "J-1", "--dominio", "localhost",
            "--desde", "2026-12-01", "--ambiente", "pruebas"]);

        // Assert
        Assert.Equal(0, codigo);
        Assert.Equal(AmbienteLicencia.Pruebas, Leer(Ruta("emitidas/LIC-2026-0001.json"), Ruta("prueba-publica.pem")).Ambiente);
    }

    // --- renovar ---

    [Fact]
    public void Renovar_tarde_cubre_desde_el_vencimiento_anterior_y_conserva_los_datos()
    {
        // Arrange: la anterior vencio el 30/11/2026 y hoy es 10/04/2027 (pasada la gracia de 2 meses)
        var clave = ClavePrivada();
        Correr(Emision(clave, "--desde", "2025-12-01", "--ambiente", "pruebas", "--gracia", "2"));
        var anterior = Ruta("emitidas/LIC-2025-0001.json");

        // Act
        var codigo = Correr("renovar", "--anterior", anterior, "--clave", clave);

        // Assert
        Assert.Equal(0, codigo);
        var renovada = Leer(Ruta("emitidas/LIC-2026-0001.json"), Ruta("prueba-publica.pem"));
        Assert.Equal(new DateOnly(2026, 12, 1), renovada.VigenteDesde);   // no "hoy": los meses de atraso no se regalan
        Assert.Equal(new DateOnly(2027, 11, 30), renovada.VigenteHasta);
        Assert.Equal("Fiduciaria Uno, C.A.", renovada.Cliente);
        Assert.Equal(["fiduciaapi.uno.com"], renovada.Dominios);
        Assert.Equal(AmbienteLicencia.Pruebas, renovada.Ambiente);
        Assert.Equal(2, renovada.MesesGracia);
        Assert.Contains("Aviso: la renovación cubre desde el 01/12/2026", _salida.ToString());
    }

    [Fact]
    public void Renovar_puede_cambiar_los_dominios()
    {
        // Arrange
        var clave = ClavePrivada();
        Correr(Emision(clave));

        // Act
        var codigo = Correr("renovar", "--anterior", Ruta("emitidas/LIC-2026-0001.json"), "--clave", clave,
            "--dominio", "fiducia-nueva.uno.com");

        // Assert
        Assert.Equal(0, codigo);
        Assert.Equal(["fiducia-nueva.uno.com"],
            Leer(Ruta("emitidas/LIC-2027-0001.json"), Ruta("prueba-publica.pem")).Dominios);
    }

    [Fact]
    public void Renovar_rechaza_una_licencia_que_no_firmo_esta_clave()
    {
        // Arrange
        var clave = ClavePrivada();
        Correr("generar-claves", "--nombre", "otra");
        Correr(Emision(Ruta("otra-privada.pem")));

        // Act
        var codigo = Correr("renovar", "--anterior", Ruta("emitidas/LIC-2026-0001.json"), "--clave", clave);

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("no se puede renovar con esta clave", _salida.ToString());
    }

    // --- ver ---

    [Fact]
    public void Ver_muestra_una_licencia_valida()
    {
        // Arrange
        var clave = ClavePrivada();
        Correr(Emision(clave));
        _salida.GetStringBuilder().Clear();

        // Act
        var codigo = Correr("ver", Ruta("emitidas/LIC-2026-0001.json"), "--clave-publica", Ruta("prueba-publica.pem"));

        // Assert
        Assert.Equal(0, codigo);
        Assert.Contains("Licencia válida", _salida.ToString());
        Assert.Contains("01/12/2026 al 30/11/2027", _salida.ToString());
        Assert.Contains("solo lectura desde el 01/03/2028", _salida.ToString());
    }

    [Fact]
    public void Ver_detecta_una_licencia_alterada()
    {
        // Arrange
        var clave = ClavePrivada();
        Correr(Emision(clave));
        var archivo = Ruta("emitidas/LIC-2026-0001.json");
        File.WriteAllText(archivo, File.ReadAllText(archivo).Replace("\"firma\": \"", "\"firma\": \"AA"));

        // Act
        var codigo = Correr("ver", archivo, "--clave", clave);

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("NO válida", _salida.ToString());
    }

    [Fact]
    public void Sin_comando_muestra_la_ayuda_con_codigo_de_error()
    {
        // Arrange (sin argumentos)

        // Act
        var codigo = Correr();

        // Assert
        Assert.Equal(1, codigo);
        Assert.Contains("generar-claves", _salida.ToString());
    }
}
