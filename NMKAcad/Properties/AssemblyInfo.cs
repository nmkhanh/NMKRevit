using Autodesk.AutoCAD.Runtime;
using NMKAcad;
using NMKAcad.Commands;

[assembly: ExtensionApplication(typeof(NmkAcadApp))]
[assembly: CommandClass(typeof(NmkWblockCommand))]
