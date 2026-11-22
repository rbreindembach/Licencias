using System.Security.Cryptography;

namespace Fiducia.Licencias.Emisor;

/// <summary>Par de claves EC P-256 del proveedor: generacion, lectura y huella para identificarlas sin mostrarlas.</summary>
internal static class Claves
{
    /// <summary>Genera un par nuevo: (privada PEM, publica PEM).</summary>
    public static (string Privada, string Publica) Generar()
    {
        using var clave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (clave.ExportECPrivateKeyPem(), clave.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>La clave publica que corresponde a una privada (PEM), o el error si no es una clave EC P-256.</summary>
    public static (string? Publica, string? Error) PublicaDe(string privadaPem)
    {
        using var clave = ECDsa.Create();
        try
        {
            clave.ImportFromPem(privadaPem);
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            return (null, "La clave no es una clave EC en formato PEM.");
        }

        if (clave.KeySize != 256)
        {
            return (null, "La clave tiene que ser EC P-256.");
        }

        try
        {
            // Una clave publica sola no tiene parte privada: no sirve para firmar.
            clave.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException)
        {
            return (null, "Es una clave publica: para emitir hace falta la privada.");
        }

        return (clave.ExportSubjectPublicKeyInfoPem(), null);
    }

    /// <summary>
    /// SHA-256 de la clave publica (SubjectPublicKeyInfo), en hexadecimal de a pares: para comparar la clave con la que se
    /// firmo contra la que esta compilada en Fiducia (ClavesProveedor) sin pegar claves en tickets o correos.
    /// </summary>
    public static string Huella(string publicaPem)
    {
        using var clave = ECDsa.Create();
        clave.ImportFromPem(publicaPem);
        var hash = SHA256.HashData(clave.ExportSubjectPublicKeyInfo());
        return string.Join(":", hash.Take(16).Select(b => b.ToString("x2")));
    }
}
