// vireo-sign: Refora's rules key, and signing Vireo's rules files with it.
//
//   dotnet run --project tools/vireo-sign -- keygen --secret <path outside the repository>   make the key, once
//   dotnet run --project tools/vireo-sign -- sign --secret <path> src/Vireo/Assets/rules/youtube.json
//   dotnet run --project tools/vireo-sign -- verify src/Vireo/Assets/rules/youtube.json             check it as Vireo will
//
// The secret key is written where --secret says, locked with a password asked for here and never stored. Keep a copy
// somewhere safe: a lost key means a new one, and only Vireo versions that carry the new public key accept its files.
// The public key goes into Vireo at src/Vireo/rules-key.pub.
using System.Text;
using Vireo.Services;
using Vireo.Signing;

try
{
    return args switch
    {
        ["keygen", "--secret", var secret] => Keygen(secret),
        ["sign", "--secret", var secret, var file] => SignFile(secret, file),
        ["verify", var file] => VerifyFile(file),
        _ => Usage(),
    };
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("vireo-sign keygen --secret <path>\nvireo-sign sign --secret <path> <rules file>\nvireo-sign verify <rules file>");
    return 2;
}

static string Root()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src", "Vireo", "Vireo.csproj"))) return dir.FullName;
    throw new IOException("Run this inside the Vireo repository.");
}

static string PublicKeyPath() => Path.Combine(Root(), "src", "Vireo", "rules-key.pub");

static int Keygen(string secret)
{
    var full = Path.GetFullPath(secret);
    if (full.StartsWith(Root() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new IOException("The secret key must not be inside the repository. Choose a folder outside it.");
    if (File.Exists(full)) throw new IOException($"{full} already exists. Nothing was changed.");
    if (File.Exists(PublicKeyPath())) throw new IOException($"{PublicKeyPath()} already exists: Vireo already trusts a key. Nothing was changed.");
    var password = Ask("Password for the rules key (12 characters or more): ");
    if (Ask("The same password again: ") != password) throw new ArgumentException("The two passwords differ. Nothing was made.");
    var pair = Signer.Generate(password);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, pair.SecretText);
    File.WriteAllText(PublicKeyPath(), pair.PublicText);
    Console.WriteLine($"Secret key: {full}  (keep a copy somewhere safe; never share it)");
    Console.WriteLine($"Public key: {PublicKeyPath()}  (goes into Vireo; commit this one)");
    return 0;
}

static int SignFile(string secret, string file)
{
    var bytes = File.ReadAllBytes(file);
    var rules = YouTubeRules.Parse(Encoding.UTF8.GetString(bytes)) ?? throw new ArgumentException($"{file} is not a valid rules file; nothing was signed.");
    var key = Minisign.ParsePublicKey(File.ReadAllText(PublicKeyPath())) ?? throw new IOException("Vireo's public key is missing.");
    var password = Ask("Password for the rules key: ");
    var signature = Signer.Sign(File.ReadAllText(secret), password, bytes, YouTubeRules.TrustedComment(rules.Version));
    // Checked against the key Vireo carries before anything is written: a file signed with some other key is caught here.
    if (YouTubeRules.Checked(bytes, signature, key) is null) throw new ArgumentException("The signature does not match Vireo's key; nothing was written.");
    File.WriteAllText(file + ".minisig", signature);
    Console.WriteLine($"signed {file} as version {rules.Version}");
    return 0;
}

static int VerifyFile(string file)
{
    var key = Minisign.ParsePublicKey(File.ReadAllText(PublicKeyPath())) ?? throw new IOException("Vireo's public key is missing.");
    var rules = YouTubeRules.Checked(File.ReadAllBytes(file), File.ReadAllText(file + ".minisig"), key);
    if (rules is null) { Console.Error.WriteLine($"BAD  {file}: not signed with Vireo's key for its version, or not a valid rules file"); return 1; }
    Console.WriteLine($"good {file}: version {rules.Version}");
    return 0;
}

static string Ask(string prompt)
{
    Console.Write(prompt);
    var text = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
        if (!char.IsControl(k.KeyChar)) text.Append(k.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}
