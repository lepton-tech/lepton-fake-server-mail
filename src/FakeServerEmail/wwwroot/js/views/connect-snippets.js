export function buildSnippets(c) {
  return {
    config: {
      ".env": `SMTP_HOST=localhost
SMTP_PORT=${c.smtp}
SMTP_SECURE=false
SMTP_USER=${c.user}
SMTP_PASSWORD=${c.pass}
SMTP_FROM=app@teste.local
IMAP_HOST=localhost
IMAP_PORT=${c.imap}
IMAP_SECURE=false
POP3_HOST=localhost
POP3_PORT=${c.pop}
POP3_SECURE=false
OAUTH_TOKEN_URL=${c.tokenUrl}
OAUTH_CLIENT_ID=minha-app
OAUTH_CLIENT_SECRET=segredo
OAUTH_SCOPE=https://outlook.office.com/.default`,
      "appsettings.json (.NET)": JSON.stringify({
        Email: {
          Smtp: { Host: "localhost", Port: c.smtpNumber, EnableSsl: false, User: c.user, Password: c.pass, From: "app@teste.local" },
          Imap: { Host: "localhost", Port: c.imapNumber, UseSsl: false, User: c.user, Password: c.pass },
          Pop3: { Host: "localhost", Port: c.popNumber, UseSsl: false, User: c.user, Password: c.pass },
          OAuth: { Authority: c.authority, TokenEndpoint: c.tokenUrl, ClientId: "minha-app", ClientSecret: "segredo", Scope: "https://outlook.office.com/.default" },
        },
      }, null, 2),
      "Spring Boot": `spring.mail.host=localhost
spring.mail.port=${c.smtp}
spring.mail.username=${c.user}
spring.mail.password=${c.pass}
spring.mail.properties.mail.smtp.auth=true
spring.mail.properties.mail.smtp.starttls.enable=false`,
      "Django (settings.py)": `EMAIL_BACKEND = "django.core.mail.backends.smtp.EmailBackend"
EMAIL_HOST = "localhost"
EMAIL_PORT = ${c.smtp}
EMAIL_HOST_USER = "${c.user}"
EMAIL_HOST_PASSWORD = "${c.pass}"
EMAIL_USE_TLS = False
EMAIL_USE_SSL = False
DEFAULT_FROM_EMAIL = "app@teste.local"`,
      "Outlook / Thunderbird": `Configuração manual da conta
E-mail: ${c.user}
Senha: ${c.pass}
Recebimento IMAP: localhost, porta ${c.imap}, sem criptografia (ou porta ${c.imaps} com SSL/TLS)
Recebimento POP3: localhost, porta ${c.pop}, sem criptografia (ou porta ${c.pops} com SSL/TLS)
Envio SMTP: localhost, porta ${c.smtp}, sem criptografia (ou porta ${c.smtps} com SSL/TLS), autenticação por senha normal
Ao usar SSL/TLS, aceite o aviso de certificado (ele é autoassinado).`,
    },

    smtp: {
      "C# (MailKit)": `using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

var mensagem = new MimeMessage();
mensagem.From.Add(MailboxAddress.Parse("app@teste.local"));
mensagem.To.Add(MailboxAddress.Parse("cliente@teste.local"));
mensagem.Subject = "Olá";
mensagem.Body = new TextPart("plain") { Text = "Mensagem de teste" };

using var smtp = new SmtpClient();
await smtp.ConnectAsync("localhost", ${c.smtp}, SecureSocketOptions.None);
await smtp.AuthenticateAsync("${c.user}", "${c.pass}");
await smtp.SendAsync(mensagem);
await smtp.DisconnectAsync(true);`,
      "C# (System.Net.Mail)": `using var cliente = new System.Net.Mail.SmtpClient("localhost", ${c.smtp})
{
    Credentials = new System.Net.NetworkCredential("${c.user}", "${c.pass}"),
};
cliente.Send("app@teste.local", "cliente@teste.local", "Olá", "Mensagem de teste");`,
      "Python": `import smtplib
from email.message import EmailMessage

msg = EmailMessage()
msg["From"] = "app@teste.local"
msg["To"] = "cliente@teste.local"
msg["Subject"] = "Olá"
msg.set_content("Mensagem de teste")

with smtplib.SMTP("localhost", ${c.smtp}) as s:
    s.login("${c.user}", "${c.pass}")
    s.send_message(msg)`,
      "Node.js": `const nodemailer = require("nodemailer");

const transporte = nodemailer.createTransport({
  host: "localhost",
  port: ${c.smtp},
  secure: false,
  auth: { user: "${c.user}", pass: "${c.pass}" },
  tls: { rejectUnauthorized: false },
});

await transporte.sendMail({ from: "app@teste.local", to: "cliente@teste.local", subject: "Olá", text: "Mensagem de teste" });`,
      "PowerShell": `Send-MailMessage -SmtpServer localhost -Port ${c.smtp} -From app@teste.local -To cliente@teste.local -Subject "Olá" -Body "Mensagem de teste" -Encoding UTF8`,
      "curl": `curl.exe smtp://localhost:${c.smtp} --mail-from app@teste.local --mail-rcpt cliente@teste.local --upload-file mensagem.eml`,
    },

    read: {
      "C# IMAP (MailKit)": `using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

using var imap = new ImapClient();
await imap.ConnectAsync("localhost", ${c.imap}, SecureSocketOptions.None);
await imap.AuthenticateAsync("${c.user}", "${c.pass}");
await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
foreach (var uid in await imap.Inbox.SearchAsync(SearchQuery.NotSeen))
{
    var mensagem = await imap.Inbox.GetMessageAsync(uid);
    Console.WriteLine(mensagem.Subject);
}
await imap.DisconnectAsync(true);`,
      "C# POP3 (MailKit)": `using MailKit.Net.Pop3;
using MailKit.Security;

using var pop = new Pop3Client();
await pop.ConnectAsync("localhost", ${c.pop}, SecureSocketOptions.None);
await pop.AuthenticateAsync("${c.user}", "${c.pass}");
for (var i = 0; i < pop.Count; i++)
    Console.WriteLine((await pop.GetMessageAsync(i)).Subject);
await pop.DisconnectAsync(true);`,
      "Python IMAP": `import imaplib, email

with imaplib.IMAP4("localhost", ${c.imap}) as imap:
    imap.login("${c.user}", "${c.pass}")
    imap.select("INBOX")
    _, ids = imap.search(None, "ALL")
    for num in ids[0].split():
        _, data = imap.fetch(num, "(RFC822)")
        print(email.message_from_bytes(data[0][1])["Subject"])`,
      "Python POP3": `import poplib, email

pop = poplib.POP3("localhost", ${c.pop})
pop.user("${c.user}")
pop.pass_("${c.pass}")
for i in range(1, len(pop.list()[1]) + 1):
    print(email.message_from_bytes(b"\\r\\n".join(pop.retr(i)[1]))["Subject"])
pop.quit()`,
      "curl": `curl.exe "imap://localhost:${c.imap}/INBOX" -u ${c.user}:${c.pass} -X "SEARCH ALL"
curl.exe "imap://localhost:${c.imap}/INBOX;UID=1" -u ${c.user}:${c.pass}
curl.exe "pop3://localhost:${c.pop}/1" -u ${c.user}:${c.pass}`,
    },

    oauth: {
      "curl (token)": `curl.exe -s -X POST ${c.tokenUrl} -d grant_type=client_credentials -d client_id=minha-app -d client_secret=segredo -d "scope=https://outlook.office.com/.default"`,
      "C# (MailKit + XOAUTH2)": `using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;

using var http = new HttpClient();
var resposta = await http.PostAsync("${c.tokenUrl}", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
    ["client_id"] = "minha-app",
    ["client_secret"] = "segredo",
    ["scope"] = "https://outlook.office.com/.default",
}));
var token = (await resposta.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["access_token"].ToString();

using var smtp = new SmtpClient();
await smtp.ConnectAsync("localhost", ${c.smtp}, SecureSocketOptions.None);
await smtp.AuthenticateAsync(new SaslMechanismOAuth2("teste@teste.local", token));`,
      "Python (XOAUTH2)": `import base64, json, smtplib, urllib.parse, urllib.request

dados = urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": "minha-app", "scope": "https://outlook.office.com/.default"}).encode()
token = json.load(urllib.request.urlopen("${c.tokenUrl}", dados))["access_token"]

auth = base64.b64encode(f"user=teste@teste.local\\x01auth=Bearer {token}\\x01\\x01".encode()).decode()
with smtplib.SMTP("localhost", ${c.smtp}) as s:
    s.ehlo()
    s.docmd("AUTH", "XOAUTH2 " + auth)
    s.sendmail("teste@teste.local", ["cliente@teste.local"], "Subject: OAuth\\r\\n\\r\\nFuncionou!")`,
      "Estilo Microsoft (MSAL)": `using Microsoft.Identity.Client;

var app = ConfidentialClientApplicationBuilder.Create("minha-app")
    .WithClientSecret("segredo")
    .WithOidcAuthority("${c.authority}")
    .Build();
var resultado = await app.AcquireTokenForClient(["https://outlook.office.com/.default"]).ExecuteAsync();`,
    },

    mcp: {
      "Claude Code (HTTP)": `claude mcp add --transport http fake-email ${c.mcpHttp}`,
      "Claude Code (stdio)": `claude mcp add fake-email -- "${c.exe}" --mcp`,
      "Claude Desktop": JSON.stringify({ mcpServers: { "fake-email": { command: c.exe, args: ["--mcp"] } } }, null, 2),
      "VS Code (.vscode/mcp.json)": JSON.stringify({ servers: { "fake-email": { type: "http", url: c.mcpHttp } } }, null, 2),
      "Cursor (.cursor/mcp.json)": JSON.stringify({ mcpServers: { "fake-email": { url: c.mcpHttp } } }, null, 2),
      "Codex (config.toml)": `[mcp_servers.fake-email]
command = '${c.exe}'
args = ["--mcp"]`,
    },
  };
}

export const MCP_TOOLS = [
  "server_info", "list_messages", "get_message", "wait_for_message", "get_message_source", "get_attachment", "send_email",
  "delete_messages", "mark_messages", "create_oauth_token", "list_oauth_tokens", "revoke_oauth_token", "list_connections",
  "get_connection_log", "update_settings",
];
