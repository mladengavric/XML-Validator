using System.Text;
using XmlValidator.Core.Schemas;

namespace XmlValidator.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Poruke su na srpskom (č, ć, š, ž, đ); bez ovoga ih stara konzola prikazuje pogrešno.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var app = new CliApplication(Console.Out, Console.Error, SchemaStore.GetDefaultRootPath());
        return await app.RunAsync(args, cancellation.Token).ConfigureAwait(false);
    }
}
