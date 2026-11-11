<p align="center"><img src="assets/icon-256.png" width="128" alt="Icono de GlassBell"></p>

<h1 align="center">GlassBell</h1>

<p align="center">Las notificaciones de todas las aplicaciones como banners de vidrio rápidos, al estilo de macOS. Para Windows 11.</p>

<p align="center"><a href="README.md">English</a> · <a href="README.zh.md">中文</a> · <a href="README.hi.md">हिन्दी</a> · <b>Español</b> · <a href="README.ar.md">العربية</a> · <a href="README.ru.md">Русский</a></p>

---

GlassBell funciona en segundo plano, recoge cada notificación que recibe Windows y la muestra como un banner de vidrio en la esquina superior derecha de la pantalla: una lente que curva el fondo en los bordes, un borde de color, brillos a lo largo del contorno y una sombra suave que se adapta a lo que hay detrás.

## Funciones

- **Todas las aplicaciones.** GlassBell lee las notificaciones de todas las aplicaciones (el agente de escucha de notificaciones de Windows) y las muestra como banners propios, hasta cuatro apilados a la vez.
- **Vidrio de verdad, dibujado por la tarjeta gráfica.** Refracción en los bordes, dispersión, brillos, tinte y sombra adaptativos. Cuatro niveles de calidad y un fondo en tiempo real opcional que sigue lo que hay detrás del banner incluso mientras se mueve.
- **A la frecuencia de la pantalla.** La tarjeta gráfica toma la imagen de la pantalla detrás de los banners y los dibuja de una vez, así que el vidrio sigue el ritmo de una pantalla de 60, 144 o 240 Hz (o más) y el procesador casi no trabaja. El renderizado clásico de la versión 1.0 sigue disponible en la configuración y entra en acción por sí solo si la tarjeta gráfica no puede hacerlo.
- **Cerrar uno o todos.** Una × redonda sobre la esquina del banner bajo el puntero lo cierra, y con dos o más banners aparece debajo de ellos un botón **Cerrar todo**. También está en el menú de la bandeja y como `GlassBell.exe --close-all`.
- **Rápido.** Un banner aparece en pantalla 30–60 ms después de que llega la notificación (medido en el PC del desarrollador).
- **Un clic abre el origen.** Un clic en un banner abre aquello a lo que apunta la notificación, igual que un clic en la de Windows.
- **Sonido.** Tres melodías sintetizadas en código (sin archivos de audio); el volumen puede seguir al del sistema.
- **Configuración con vista previa en vivo**, icono en la bandeja, inicio con Windows.
- **20 idiomas.** La interfaz usa el idioma de Windows (si no está entre ellos, inglés) o el idioma elegido al pie de la configuración.
- **Ligero.** Casi nada en reposo, unos pocos por ciento de un núcleo mientras hay banners en pantalla y unos 100 MB de memoria, también después de cientos de banners (medido en el PC del desarrollador).

## Instalación

1. Descarga **GlassBell-Setup.exe** desde [Releases](https://github.com/vibegineer/GlassBell/releases/latest) y ejecútalo. No hacen falta permisos de administrador.
   El instalador no tiene firma digital, así que SmartScreen puede mostrar «Windows protegió su PC»: haz clic en **Más información → Ejecutar de todas formas**.
2. Activa **No molestar** (Configuración → Sistema → Notificaciones) para que los banners de Windows no aparezcan junto a los de GlassBell. Con No molestar activado las notificaciones siguen llegando, y GlassBell las sigue viendo.

El icono junto al reloj abre la configuración. Para quitar GlassBell: Configuración → Aplicaciones → Aplicaciones instaladas → GlassBell → Desinstalar.

**Requisitos:** Windows 11 de 64 bits. Todo lo demás (.NET Framework 4.8, Direct3D 11) ya forma parte de Windows.

## Línea de comandos

GlassBell también puede mostrar banners para tus propios scripts:

```
GlassBell.exe --title "Compilación terminada" --body "Pasaron las 214 pruebas" --app "CI"
```

| Comando | Qué hace |
|---|---|
| `GlassBell.exe` | inicia GlassBell si hace falta y abre la configuración |
| `--title T --body B [--app A] [--app-id AUMID] [--seconds S]` | muestra un banner (`--app-id` toma el icono de una aplicación instalada) |
| `--test` | muestra dos banners de ejemplo |
| `--close-all` | cierra todos los banners de la pantalla |
| `--settings` / `--quit` | abre la configuración / cierra GlassBell |
| `--install` / `--uninstall` | activa / desactiva el inicio con Windows (para una copia usada sin el instalador) |
| `--stats` | escribe en el registro cuánta memoria usa la copia en ejecución |

## Archivos

| Qué | Dónde |
|---|---|
| Programa | `%LOCALAPPDATA%\Programs\GlassBell` |
| Configuración | `%APPDATA%\GlassBell\settings.ini` |
| Registro | `%LOCALAPPDATA%\GlassBell\log.txt` |

El idioma elegido en la configuración se guarda en `settings.ini` como `lang=es` (o cualquier otro código de `lang/`); `lang=auto` sigue a Windows.
Un archivo `portable.flag` junto a `GlassBell.exe` hace que la configuración y el registro se guarden en la carpeta del programa.

## Compilación

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Esto genera `bin\GlassBell.exe` y `dist\GlassBell-Setup.exe` con el compilador de C# del .NET Framework que viene con Windows: sin SDK, sin NuGet, sin Visual Studio.

| Carpeta | Contenido |
|---|---|
| `src/` | el programa: banners, el renderizado en la tarjeta gráfica (`GpuEngine.cs`, `Dx.cs`), el renderizado clásico del vidrio, la captura de notificaciones, la configuración, los sonidos |
| `setup/` | el instalador y el desinstalador |
| `lang/` | los textos de la interfaz, un archivo por idioma |
| `assets/` | el icono |
| `tools/` | scripts de prueba y medición usados durante el desarrollo, el generador del icono |

## Traducciones

Las traducciones, esta página incluida, se hicieron con IA y solo se revisó que estén completas y quepan, no las leyeron hablantes nativos. Las correcciones son bienvenidas: cada idioma es un archivo de texto en [`lang/`](lang), y [`lang/en.txt`](lang/en.txt) explica cada línea.

## Apoyo

GlassBell es gratuito. Si te resulta útil, puedes apoyar su desarrollo en [Patreon](https://www.patreon.com/vibegineer).

## Licencia

[MIT](LICENSE). macOS es una marca comercial de Apple Inc.; GlassBell no está afiliado a Apple.
