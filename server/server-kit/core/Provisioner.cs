using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrefabEditor
{
    // First-run self-provisioning of the console trust store. A fresh install
    // (no trusted public key) gets a per-server RSA keypair, allowlist, and port
    // file in exactly the layout attconsole.exe provisions, so attcmd.py and the
    // bot's tokenSigner.js work against a brand-new server with zero manual steps.
    // An already-provisioned server is left completely untouched.
    //
    // Pure BCL on purpose - the offline test harness compiles this file standalone.
    public static class Provisioner
    {
        public const int DefaultConsolePort = 1764;

        public static void Run(string keyDir, string gameDir, Action<string> log)
        {
            string trusted = Path.Combine(keyDir, "trusted");
            string cli = Path.Combine(keyDir, "cli");
            Directory.CreateDirectory(trusted);
            Directory.CreateDirectory(cli);

            string allowlist = Path.Combine(keyDir, "allowlist.txt");
            if (!File.Exists(allowlist))
            {
                File.WriteAllText(allowlist, "1" + Environment.NewLine);
                log("allowlist.txt created (UserId 1).");
            }

            string portFile = Path.Combine(keyDir, "console_port.txt");
            if (!File.Exists(portFile))
                File.WriteAllText(portFile, DefaultConsolePort.ToString());

            // The trust set is the decider: any trusted key present means this server
            // is provisioned (regenerating here would ADD a signer - never do that).
            if (Directory.GetFiles(trusted, "*.pub.xml").Length > 0) return;

            log("No trusted console key found - generating this server's keypair (one-time)...");
            string kid = RandomHex(8);
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);
            try
            {
                rsa.PersistKeyInCsp = false;
                File.WriteAllText(Path.Combine(cli, kid + ".priv.xml"), rsa.ToXmlString(true));
                File.WriteAllText(Path.Combine(trusted, kid + ".pub.xml"), rsa.ToXmlString(false));
            }
            finally
            {
                rsa.Clear();
            }

            // attconsole.exe-compatible config so the Windows CLI adopts this key
            // instead of provisioning a second one. tokenSigner.js ignores it.
            WriteCliConfig(Path.Combine(cli, "config.json"), gameDir, kid);

            log("Console keypair provisioned (kid " + kid + "). Private: cli\\" + kid
                + ".priv.xml  Trusted: trusted\\" + kid + ".pub.xml");
        }

        static void WriteCliConfig(string path, string gameDir, string kid)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"GameDir\": \"").Append(JsonEscape(gameDir)).Append("\",\n");
            sb.Append("  \"ServerUrl\": \"http://127.0.0.1:").Append(DefaultConsolePort).Append("/console\",\n");
            sb.Append("  \"Kid\": \"").Append(kid).Append("\",\n");
            sb.Append("  \"OwnerUserId\": 1,\n");
            sb.Append("  \"OwnerUsername\": \"Owner\",\n");
            sb.Append("  \"Policy\": \"game_access_public,server_access_pre_alpha,server_access_tutorial\",\n");
            sb.Append("  \"TokenLifetimeSeconds\": 900\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString());
        }

        static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string RandomHex(int byteCount)
        {
            byte[] b = new byte[byteCount];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}
