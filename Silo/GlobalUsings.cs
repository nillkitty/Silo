global using File = System.IO.File;
global using System.Windows;
global using Telefrag.Common;
global using System;
global using System.Collections.Generic;
global using System.Threading.Tasks;
global using System.Text;
global using System.IO;
global using Silo;
global using Silo.Contracts;
global using Silo.Contracts.Connectors;
global using Silo.Contracts.Core;
global using Silo.Providers.Data;
global using Silo.Contracts.Resources;
global using Silo.Contracts.UI;
global using ILogger = Serilog.ILogger;
global using Window = System.Windows.Window;
using System.Windows.Markup;

[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Contracts")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Oroviders")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Contracts.Core")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.UI")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.MV")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.World")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Universal")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Contracts.UI")]
[assembly: XmlnsDefinition("https://schema.nill.dev/Silo", "Silo.Shell")]
[assembly: XmlnsPrefix("https://schema.nill.dev/Silo", "s")]

namespace Silo;