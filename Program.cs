using MAS_BT.Examples;

namespace MAS_BT
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Entry Point: Wähle welches Beispiel ausgeführt werden soll
            if (args.Length > 0 && args[0] == "--example")
            {
                if (args.Length > 1)
                {
                    switch (args[1].ToLower())
                    {
                        case "resource-init":
                        case "resource":
                            await ResourceHolonInitialization.Run(args.Skip(2).ToArray());
                            break;
                        case "module-init":
                        case "module-init-test":
                            await ModuleInitializationTestRunner.Run(args.Skip(2).ToArray());
                            break;
                        default:
                            Console.WriteLine($"Unbekanntes Beispiel: {args[1]}");
                            ShowHelp();
                            break;
                    }
                }
                else
                {
                    ShowHelp();
                }
            }
            else
            {
                // Default: Module Initialization Test
                await ModuleInitializationTestRunner.Run(args);
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("MAS-BT Examples");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run                                    # Run default (Module Init Test)");
            Console.WriteLine("  dotnet run --example module-init-test         # Module Initialization Test");
            Console.WriteLine("  dotnet run --example resource-init            # Resource Holon Initialization");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  module-init-test : Debug ModuleInitializationTest.bt.xml");
            Console.WriteLine("  resource-init    : Initialize a Resource Holon with all Configuration Nodes");
            Console.WriteLine();
        }
    }
}
