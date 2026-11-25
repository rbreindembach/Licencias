using System.Globalization;
using Fiducia.BuildingBlocks.Licencias;

namespace Fiducia.Licencias.Emisor;

/// <summary>
/// Donde trabaja el emisor: carpeta de trabajo (registro y licencias emitidas), variables de entorno y la fecha de hoy.
/// Separado para poder probar los comandos sin tocar la maquina.
/// </summary>
internal sealed record Entorno(
    string CarpetaPorDefecto, Func<string, string?> Variable, DateOnly Hoy, string? CarpetaPorDefectoCaja = null)
{
    public const string VariableCarpeta = "FIDUCIA_LICENCIAS_CARPETA";
    public const string VariableClave = "FIDUCIA_LICENCIAS_CLAVE";

    public static Entorno DelSistema()
    {
        var perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(
            Path.Combine(perfil, ".fiducia", "licencias"),
            Environment.GetEnvironmentVariable,
            DateOnly.FromDateTime(DateTime.Today),
            Path.Combine(perfil, ".caja", "licencias"));
    }
}

/// <summary>
/// Para que sistema se emite. Los dos usan el mismo formato de archivo (la biblioteca de Fiducia; Caja 2000 tiene una
/// copia identica en Caja.Shared.Licencias), asi que lo que separa una licencia de otra es la CLAVE: cada sistema solo
/// acepta las claves publicas compiladas en el, y cada producto tiene su par, su carpeta de trabajo (registro y
/// emitidas) y su prefijo de Id.
/// </summary>
internal sealed record Producto(
    string Codigo, string Nombre, string PrefijoId, string VariableCarpeta, string VariableClave,
    string DondeVaLaClavePublica)
{
    public static readonly Producto Fiducia = new(
        "fiducia", "Fiducia", "LIC", Entorno.VariableCarpeta, Entorno.VariableClave,
        "ClavesProveedor.Publicas en Fiducia (src/Host/Fiducia.Api/Licenciamiento/)");

    public static readonly Producto Caja = new(
        "caja", "Caja 2000", "CAJA", "CAJA_LICENCIAS_CARPETA", "CAJA_LICENCIAS_CLAVE",
        "ClavesProveedor.Publicas en Caja 2000 (src/Shared/Caja.Shared.Licenciamiento/ClavesProveedor.cs)");

    public static Producto? Buscar(string? codigo) => (codigo?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "fiducia" => Fiducia,
        "caja" or "caja2000" => Caja,
        _ => null,
    };
}

/// <summary>
/// Los comandos del emisor. Cada uno devuelve el codigo de salida (0 = bien) y escribe en <paramref name="salida"/>; los
/// errores del usuario (opciones, archivos, claves) son mensajes, no excepciones.
/// </summary>
internal sealed class Emisor(Entorno entorno, TextWriter salida)
{
    private const int GraciaPorDefecto = 3;

    private Producto _producto = Producto.Fiducia;

    private static readonly Dictionary<string, string[]> OpcionesValidas = new()
    {
        ["generar-claves"] = ["nombre", "carpeta", "producto"],
        ["emitir"] = ["cliente", "rif", "dominio", "desde", "hasta", "ambiente", "gracia", "id", "salida", "clave", "carpeta", "producto"],
        ["renovar"] = ["anterior", "dominio", "gracia", "id", "salida", "clave", "carpeta", "producto"],
        ["ver"] = ["clave-publica", "clave", "producto"],
    };

    public int Ejecutar(IReadOnlyList<string> args)
    {
        var (argumentos, error) = Argumentos.Leer(args);
        if (argumentos is null)
        {
            return Fallar(error!);
        }

        if (argumentos.Comando is null or "ayuda" or "--help" or "-h")
        {
            Ayuda();
            return argumentos.Comando is null ? 1 : 0;
        }

        if (!OpcionesValidas.TryGetValue(argumentos.Comando, out var validas))
        {
            return Fallar($"Comando desconocido: {argumentos.Comando}. Use \"ayuda\".");
        }

        var desconocida = argumentos.Nombres.FirstOrDefault(n => !validas.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (desconocida is not null)
        {
            return Fallar($"Opción desconocida para {argumentos.Comando}: --{desconocida}.");
        }

        if (Producto.Buscar(argumentos.Opcion("producto")) is not { } producto)
        {
            return Fallar($"--producto tiene que ser fiducia o caja (recibido \"{argumentos.Opcion("producto")}\").");
        }

        _producto = producto;
        return argumentos.Comando switch
        {
            "generar-claves" => GenerarClaves(argumentos),
            "emitir" => Emitir(argumentos),
            "renovar" => Renovar(argumentos),
            _ => Ver(argumentos),
        };
    }

    // ------------------------------------------------------------------ generar-claves

    private int GenerarClaves(Argumentos a)
    {
        var carpeta = Carpeta(a);
        var nombre = a.Opcion("nombre") ?? $"clave-{entorno.Hoy.Year}";
        var privada = Path.Combine(carpeta, $"{nombre}-privada.pem");
        var publica = Path.Combine(carpeta, $"{nombre}-publica.pem");
        if (File.Exists(privada) || File.Exists(publica))
        {
            return Fallar($"Ya existe una clave \"{nombre}\" en {carpeta}: no se sobrescribe. Use otro --nombre.");
        }

        var (pemPrivada, pemPublica) = Claves.Generar();
        Directory.CreateDirectory(carpeta);
        EscribirPrivada(privada, pemPrivada);
        File.WriteAllText(publica, pemPublica);

        salida.WriteLine($"Clave privada: {privada}");
        salida.WriteLine($"Clave pública: {publica}");
        salida.WriteLine($"Huella:        {Claves.Huella(pemPublica)}");
        salida.WriteLine();
        salida.WriteLine($"Agregue esta clave pública a {_producto.DondeVaLaClavePublica}:");
        salida.WriteLine(pemPublica);
        salida.WriteLine("Guarde la clave privada en custodia (Key Vault, bóveda) y no la copie a ningún repositorio.");
        return 0;
    }

    // ------------------------------------------------------------------ emitir

    private int Emitir(Argumentos a)
    {
        var faltantes = new[] { "cliente", "rif", "dominio", "desde", "ambiente" }.Where(o => a.Opcion(o) is null).ToList();
        if (faltantes.Count > 0)
        {
            return Fallar($"Faltan opciones: {string.Join(", ", faltantes.Select(o => "--" + o))}.");
        }

        if (LeerFecha(a.Opcion("desde")!, "desde") is not { } desde)
        {
            return 1;
        }

        DateOnly hasta;
        if (a.Opcion("hasta") is { } textoHasta)
        {
            if (LeerFecha(textoHasta, "hasta") is not { } h)
            {
                return 1;
            }

            hasta = h;
        }
        else
        {
            hasta = desde.AddYears(1).AddDays(-1);
        }

        if (LeerAmbiente(a.Opcion("ambiente")!) is not { } ambiente || LeerGracia(a.Opcion("gracia")) is not { } gracia ||
            LeerDominios(a.Opciones("dominio")) is not { } dominios)
        {
            return 1;
        }

        return EmitirLicencia(a, a.Opcion("cliente")!.Trim(), a.Opcion("rif")!.Trim(), dominios, desde, hasta, gracia, ambiente);
    }

    // ------------------------------------------------------------------ renovar

    private int Renovar(Argumentos a)
    {
        if (a.Opcion("anterior") is not { } archivoAnterior)
        {
            return Fallar("Falta --anterior (el archivo de la licencia que se renueva).");
        }

        if (!File.Exists(archivoAnterior))
        {
            return Fallar($"No existe {archivoAnterior}.");
        }

        if (LeerClavePrivada(a) is not { } clave)
        {
            return 1;
        }

        var lectura = ArchivoLicencia.Leer(File.ReadAllText(archivoAnterior), clave.Publica);
        if (lectura.Licencia is not { } anterior)
        {
            return Fallar($"La licencia anterior no se puede renovar con esta clave: {lectura.Mensaje}");
        }

        var dominios = anterior.Dominios;
        if (a.Opciones("dominio").Count > 0)
        {
            if (LeerDominios(a.Opciones("dominio")) is not { } nuevos)
            {
                return 1;
            }

            dominios = nuevos;
        }

        var gracia = anterior.MesesGracia;
        if (a.Opcion("gracia") is { } textoGracia)
        {
            if (LeerGracia(textoGracia) is not { } g)
            {
                return 1;
            }

            gracia = g;
        }

        // Los meses de atraso no se regalan: la renovacion empieza al dia siguiente del vencimiento anterior.
        var (desde, hasta) = Licencia.PeriodoRenovacion(anterior);
        if (desde < entorno.Hoy)
        {
            salida.WriteLine($"Aviso: la renovación cubre desde el {desde:dd/MM/yyyy} (vencimiento anterior), aunque hoy sea {entorno.Hoy:dd/MM/yyyy}.");
        }

        return EmitirLicencia(a, anterior.Cliente, anterior.Rif, dominios, desde, hasta, gracia, anterior.Ambiente, clave);
    }

    // ------------------------------------------------------------------ ver

    private int Ver(Argumentos a)
    {
        if (a.Sueltos.Count != 1)
        {
            return Fallar("Uso: ver <archivo> [--clave-publica <pem> | --clave <privada.pem>]");
        }

        var archivo = a.Sueltos[0];
        if (!File.Exists(archivo))
        {
            return Fallar($"No existe {archivo}.");
        }

        string publica;
        if (a.Opcion("clave-publica") is { } rutaPublica)
        {
            if (!File.Exists(rutaPublica))
            {
                return Fallar($"No existe {rutaPublica}.");
            }

            publica = File.ReadAllText(rutaPublica);
        }
        else if (LeerClavePrivada(a) is { } clave)
        {
            publica = clave.Publica;
        }
        else
        {
            return 1;
        }

        ResultadoLecturaLicencia lectura;
        try
        {
            lectura = ArchivoLicencia.Leer(File.ReadAllText(archivo), publica);
        }
        catch (ArgumentException e)
        {
            return Fallar(e.Message);
        }

        if (lectura.Licencia is not { } licencia)
        {
            return Fallar($"Licencia NO válida ({lectura.Error}): {lectura.Mensaje}");
        }

        salida.WriteLine("Licencia válida (firma verificada).");
        Describir(licencia, publica);
        return 0;
    }

    // ------------------------------------------------------------------ comun

    private int EmitirLicencia(
        Argumentos a, string cliente, string rif, IReadOnlyList<string> dominios, DateOnly desde, DateOnly hasta, int gracia,
        AmbienteLicencia ambiente, (string Privada, string Publica)? claveLeida = null)
    {
        if ((claveLeida ?? LeerClavePrivada(a)) is not { } clave)
        {
            return 1;
        }

        var carpeta = Carpeta(a);
        var registro = new RegistroEmisiones(Path.Combine(carpeta, "registro.csv"), _producto.PrefijoId);
        var id = a.Opcion("id")?.Trim() ?? registro.SiguienteId(desde.Year);
        if (registro.Existe(id))
        {
            return Fallar($"La licencia {id} ya fue emitida (ver {Path.Combine(carpeta, "registro.csv")}).");
        }

        var licencia = new Licencia(id, cliente, rif, dominios, desde, hasta, gracia, entorno.Hoy, ambiente);
        string contenido;
        try
        {
            contenido = ArchivoLicencia.Emitir(licencia, clave.Privada);
        }
        catch (ArgumentException e)
        {
            return Fallar($"La licencia no es válida: {e.Message}");
        }

        // Control: lo emitido se verifica con la clave publica, igual que lo hara el sistema.
        if (!ArchivoLicencia.Leer(contenido, clave.Publica).Exito)
        {
            return Fallar("La licencia emitida no pasa su propia verificación; no se guardó.");
        }

        var archivo = a.Opcion("salida") ?? Path.Combine(carpeta, "emitidas", $"{id}.json");
        if (File.Exists(archivo))
        {
            return Fallar($"Ya existe {archivo}: no se sobrescribe.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivo))!);
        File.WriteAllText(archivo, contenido);
        registro.Agregar(licencia, archivo);

        salida.WriteLine($"Licencia emitida: {archivo}");
        Describir(licencia, clave.Publica);
        return 0;
    }

    private void Describir(Licencia l, string publica)
    {
        salida.WriteLine($"  Producto:         {_producto.Nombre}");
        salida.WriteLine($"  Id:               {l.Id}");
        salida.WriteLine($"  Cliente:          {l.Cliente} ({l.Rif})");
        salida.WriteLine($"  Dominios:         {string.Join(", ", l.Dominios)}");
        salida.WriteLine($"  Vigencia:         {l.VigenteDesde:dd/MM/yyyy} al {l.VigenteHasta:dd/MM/yyyy}");
        salida.WriteLine($"  Gracia:           {l.MesesGracia} meses (solo lectura desde el {l.InicioSoloLectura:dd/MM/yyyy})");
        salida.WriteLine($"  Ambiente:         {(l.Ambiente == AmbienteLicencia.Produccion ? "Producción" : "Pruebas")}");
        salida.WriteLine($"  Emitida:          {l.Emitida:dd/MM/yyyy}");
        salida.WriteLine($"  Huella de clave:  {Claves.Huella(publica)}");
    }

    /// <summary>Carpeta de trabajo: --carpeta, la variable del producto o la carpeta por defecto del producto.</summary>
    private string Carpeta(Argumentos a) =>
        a.Opcion("carpeta") ?? entorno.Variable(_producto.VariableCarpeta) ??
        (_producto == Producto.Caja
            ? entorno.CarpetaPorDefectoCaja ?? Path.Combine(entorno.CarpetaPorDefecto, "caja")
            : entorno.CarpetaPorDefecto);

    /// <summary>La clave privada de --clave o de la variable del producto (ruta al PEM), con su publica.</summary>
    private (string Privada, string Publica)? LeerClavePrivada(Argumentos a)
    {
        var ruta = a.Opcion("clave") ?? entorno.Variable(_producto.VariableClave);
        if (string.IsNullOrWhiteSpace(ruta))
        {
            Fallar($"Indique la clave privada con --clave <archivo.pem> o la variable {_producto.VariableClave}.");
            return null;
        }

        if (!File.Exists(ruta))
        {
            Fallar($"No existe la clave {ruta}.");
            return null;
        }

        var privada = File.ReadAllText(ruta);
        var (publica, error) = Claves.PublicaDe(privada);
        if (publica is null)
        {
            Fallar(error!);
            return null;
        }

        return (privada, publica);
    }

    private DateOnly? LeerFecha(string texto, string opcion)
    {
        if (DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
        {
            return fecha;
        }

        Fallar($"--{opcion} tiene que ser una fecha AAAA-MM-DD (recibido \"{texto}\").");
        return null;
    }

    private AmbienteLicencia? LeerAmbiente(string texto)
    {
        switch (texto.Trim().ToLowerInvariant())
        {
            case "produccion" or "producción":
                return AmbienteLicencia.Produccion;
            case "pruebas" or "qa":
                return AmbienteLicencia.Pruebas;
            default:
                Fallar($"--ambiente tiene que ser produccion o pruebas (recibido \"{texto}\").");
                return null;
        }
    }

    private int? LeerGracia(string? texto)
    {
        if (texto is null)
        {
            return GraciaPorDefecto;
        }

        if (int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var meses) && meses <= Licencia.MaximoMesesGracia)
        {
            return meses;
        }

        Fallar($"--gracia tiene que ser un número de meses entre 0 y {Licencia.MaximoMesesGracia} (recibido \"{texto}\").");
        return null;
    }

    /// <summary>Nombres de host sin esquema, puerto ni ruta (lo que compara Fiducia con el host del request).</summary>
    private IReadOnlyList<string>? LeerDominios(IReadOnlyList<string> textos)
    {
        var dominios = new List<string>();
        foreach (var texto in textos)
        {
            var dominio = texto.Trim().ToLowerInvariant();
            if (dominio.Contains("://") || dominio.Contains('/') || dominio.Contains(':') ||
                Uri.CheckHostName(dominio) is UriHostNameType.Unknown or UriHostNameType.Basic)
            {
                Fallar($"--dominio tiene que ser solo el nombre de host, sin https://, puerto ni ruta (recibido \"{texto}\").");
                return null;
            }

            if (!dominios.Contains(dominio))
            {
                dominios.Add(dominio);
            }
        }

        return dominios;
    }

    private int Fallar(string mensaje)
    {
        salida.WriteLine($"Error: {mensaje}");
        return 1;
    }

    private void Ayuda() => salida.WriteLine($$"""
        fiducia-licencias: claves y licencias anuales de Fiducia y de Caja 2000.

          Todos los comandos aceptan --producto fiducia|caja (por defecto fiducia). Cada producto tiene su propio par de
          claves, su carpeta de trabajo y su prefijo de Id (LIC-AAAA-NNNN / CAJA-AAAA-NNNN).

          generar-claves [--nombre clave-AAAA] [--carpeta <dir>]
              Genera un par EC P-256. La pública se agrega a ClavesProveedor del sistema; la privada va a custodia.

          emitir --cliente <razón social> --rif <J-...> --dominio <host> [--dominio <host>...]
                 --desde AAAA-MM-DD [--hasta AAAA-MM-DD] --ambiente produccion|pruebas [--gracia 3]
                 [--id LIC-AAAA-NNNN] [--salida <archivo>] [--clave <privada.pem>] [--carpeta <dir>]
              Sin --hasta, un año desde --desde. Sin --id, el siguiente del registro.

          renovar --anterior <licencia.json> [--dominio <host>...] [--gracia N] [--id] [--salida] [--clave] [--carpeta]
              Mismo cliente, dominios y ambiente; vigencia desde el día siguiente al vencimiento anterior (los meses de
              atraso no se regalan).

          ver <licencia.json> [--clave-publica <publica.pem> | --clave <privada.pem>]
              Verifica la firma y muestra el contenido.

        El dominio es, en Fiducia, el host de Fiducia.Api (p.ej. fiduciaapi.cliente.com); en Caja 2000, el host público
        de la instalación (p.ej. caja.cliente.com, o localhost en una instalación local). Sin https:// ni puerto.
        Carpeta de trabajo (registro.csv y emitidas/): --carpeta, {{Entorno.VariableCarpeta}} / CAJA_LICENCIAS_CARPETA,
        o ~/.fiducia/licencias / ~/.caja/licencias.
        Clave privada: --clave o {{Entorno.VariableClave}} / CAJA_LICENCIAS_CLAVE (ruta al PEM).
        """);

    private static void EscribirPrivada(string ruta, string pem)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(ruta, pem);
            return;
        }

        // Solo el dueño puede leerla (600), desde que se crea.
        using var flujo = new FileStream(ruta, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var escritor = new StreamWriter(flujo);
        escritor.Write(pem);
    }
}
