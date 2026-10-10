global using System.Windows;
global using Telefrag.Common;
global using System;
global using System.Collections.Generic;
global using System.Threading.Tasks;
global using System.Text;
global using Silo;
global using Silo.Contracts;
global using Silo.Contracts.Connectors;
global using Silo.Contracts.Core;
global using Silo.Contracts.Providers;
global using Silo.Contracts.Providers.Archive;
global using Silo.Contracts.Providers.Crypto;
global using Silo.Contracts.Providers.Data;
global using Silo.Contracts.Providers.Data.Processing;
global using Silo.Contracts.Providers.Data.Source;
global using Silo.Contracts.Providers.Data.Validation;
global using Silo.Contracts.Providers.Document;
global using Silo.Contracts.Providers.Reflection;
global using Silo.Contracts.Resources;
global using Silo.Contracts.UI;
global using Silo.Shell;
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