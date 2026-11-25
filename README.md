# Licencias de Fiducia y Caja 2000

Herramienta del proveedor para generar las claves de firma y emitir o renovar las licencias anuales de los sistemas
Fiducia y Caja 2000 (caja de ahorros). Las reglas de la licencia (gracia, período de prueba, solo lectura, ambiente)
son las mismas en los dos y están en el `CLAUDE.md` de cada uno (Fiducia: "Licencia anual y modo solo lectura"; Caja
2000: "Annual license, read-only mode, terms of use and test environment").

## Producto: `--producto fiducia|caja`

Todos los comandos aceptan `--producto` (por defecto `fiducia`, así los usos de antes no cambian). Los dos sistemas usan
**el mismo formato de archivo** (Caja 2000 tiene una copia idéntica de la biblioteca en `Caja.Shared.Licencias`); lo
que impide que una licencia de un producto sirva en el otro es la **clave**: cada sistema compila solo sus propias
claves públicas. Por eso cada producto tiene:

| | Fiducia | Caja 2000 (`--producto caja`) |
|---|---|---|
| Carpeta de trabajo | `~/.fiducia/licencias` o `FIDUCIA_LICENCIAS_CARPETA` | `~/.caja/licencias` o `CAJA_LICENCIAS_CARPETA` |
| Clave privada | `--clave` o `FIDUCIA_LICENCIAS_CLAVE` | `--clave` o `CAJA_LICENCIAS_CLAVE` |
| Ids | `LIC-AAAA-NNNN` | `CAJA-AAAA-NNNN` |
| Dónde va la clave pública | `src/Host/Fiducia.Api/Licenciamiento/ClavesProveedor.cs` | `src/Shared/Caja.Shared.Licenciamiento/ClavesProveedor.cs` |
| Dominio | host de Fiducia.Api | host público de la instalación (`localhost` en una instalación local) |
| Clave vigente | 2026, huella `83:82:2f:29:...` | caja-2026, huella `19:f1:b9:c9:b9:01:e1:2b:4d:b9:76:56:d3:24:0f:29` |

```bash
fiducia-licencias emitir --producto caja \
  --cliente "Caja de Ahorros Ejemplo" --rif J-12345678-9 \
  --dominio caja.ejemplo.com --desde 2026-12-01 --ambiente produccion
```

La caja carga la licencia en **Seguridad > Licencia** (permiso `licencia.administrar`). Si cambia el formato del archivo
(`ArchivoLicencia.Formato`), hay que cambiarlo en las dos bibliotecas (la de Fiducia, que usa este emisor, y la copia de
Caja 2000) y actualizar el submódulo.

> **La clave privada nunca entra a este repositorio** (ni a ningún otro). Con ella cualquiera puede emitir licencias.
> El `.gitignore` excluye `*.pem`, `emitidas/` y `registro.csv`, pero la regla es no copiarla aquí.

## Preparación

```bash
git clone --recurse-submodules https://github.com/rbreindembach/Licencias.git
# o, si ya estaba clonado:
git submodule update --init
dotnet build
```

`externo/Fiducia` es el repositorio de Fiducia como submódulo **fijado a un commit**: el emisor usa la misma biblioteca
(`Fiducia.BuildingBlocks.Licencias`) que verifica las licencias en el sistema, así el formato firmado no se duplica.
Si Fiducia cambia el formato de la licencia (sube `ArchivoLicencia.Formato`), actualizar el submódulo a ese commit:

```bash
cd externo/Fiducia && git fetch && git checkout <commit> && cd ../..
git add externo/Fiducia && git commit -m "Actualiza Fiducia a <commit>"
```

## Uso

El ejecutable es `src/Fiducia.Licencias.Emisor/bin/Debug/net9.0/fiducia-licencias` (o
`dotnet run --project src/Fiducia.Licencias.Emisor -- <comando>`).

- **Carpeta de trabajo** (registro de emisiones y licencias emitidas): `--carpeta`, la variable
  `FIDUCIA_LICENCIAS_CARPETA` o, por defecto, `~/.fiducia/licencias`.
- **Clave privada**: `--clave <archivo.pem>` o la variable `FIDUCIA_LICENCIAS_CLAVE` (ruta al PEM).

### Generar un par de claves (una vez, o al rotar)

```bash
fiducia-licencias generar-claves --nombre clave-2027
```

Crea `clave-2027-privada.pem` (permisos 600) y `clave-2027-publica.pem`, y muestra la pública y su huella. La pública se
agrega a `ClavesProveedor.Publicas` en Fiducia (`src/Host/Fiducia.Api/Licenciamiento/ClavesProveedor.cs`); el sistema
acepta una licencia firmada con cualquiera de las de esa lista, así que para rotar se agrega la nueva, se despliega, y
recién después se retira la vieja. La clave vigente es la de 2026 (huella `83:82:2f:29:f3:01:5f:e3:df:4c:b3:2c:7d:ff:5f:ec`).

### Emitir una licencia nueva

```bash
fiducia-licencias emitir \
  --cliente "Fiduciaria Ejemplo, C.A." --rif J-12345678-9 \
  --dominio fiduciaapi.ejemplo.com \
  --desde 2026-12-01 --ambiente produccion
```

- `--dominio` es el **host de Fiducia.Api** (por el que llegan los pedidos y por el que el sistema se llama a sí mismo),
  sin `https://` ni puerto. Se puede repetir (p.ej. un sitio de contingencia). Para desarrollo, `localhost`.
- `--ambiente pruebas` para QA o desarrollo: el sistema muestra siempre la franja "AMBIENTE DE PRUEBAS".
- Sin `--hasta`, la vigencia es de un año. `--gracia` (meses antes del solo lectura) es 3 por defecto.
- Sin `--id`, toma el siguiente del registro (`LIC-AAAA-NNNN`, por año de inicio). Un Id no se repite: el sistema
  rechaza cargar dos veces la misma licencia.
- Queda en `<carpeta>/emitidas/<Id>.json` y anotada en `<carpeta>/registro.csv`.

### Renovar

```bash
fiducia-licencias renovar --anterior ~/.fiducia/licencias/emitidas/LIC-2026-0001.json
```

Mismo cliente, dominios, ambiente y gracia (se pueden cambiar con `--dominio` y `--gracia`). La vigencia empieza **el
día siguiente al vencimiento anterior aunque se renueve tarde**: los meses de atraso no se regalan. Solo renueva
licencias firmadas con la misma clave.

### Verificar un archivo

```bash
fiducia-licencias ver LIC-2026-0001.json --clave-publica clave-2026-publica.pem
```

## Entregar la licencia al cliente

El cliente la carga en Fiducia, en **Sistema > Licencia** (permiso `sistema.licencia.administrar`). Se puede cargar
aunque el sistema ya esté en solo lectura; si la licencia no autoriza el dominio por el que responde esa instalación, el
sistema la rechaza y dice cuál esperaba.

## Pruebas

```bash
dotnet test
```

Cada prueba usa una carpeta temporal y claves efímeras, y verifica lo emitido con la biblioteca de Fiducia.
