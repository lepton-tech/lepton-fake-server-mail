using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace FakeServerEmail.Mcp;

public static class McpSetup
{
    public const string Instructions = """
        Fake Server Email: servidor de e-mail falso rodando nesta máquina (SMTP, POP3, IMAP e OAuth 2.0) para testes.
        Todo e-mail enviado para ele fica capturado aqui; nada é entregue de verdade.
        Fluxo típico de teste: server_info para descobrir as portas, delete_messages(all=true) para limpar,
        disparar a ação no sistema testado, wait_for_message para pegar o e-mail e usar os links ou códigos do corpo.
        Se o sistema não conseguir enviar ou autenticar, use list_connections(onlyFailures=true) e get_connection_log.
        Para OAuth: create_oauth_token gera tokens aceitos via XOAUTH2/OAUTHBEARER; revoke_oauth_token testa recusa.
        """;

    public static IMcpServerBuilder AddFakeMailMcp(this IServiceCollection services, MailApiClient api)
    {
        services.AddSingleton(api);
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "fake-server-email",
                    Title = "Fake Server Email",
                    Version = FakeMailServer.Version,
                    Description = "Servidor de e-mail falso para testes locais (SMTP, POP3, IMAP e OAuth 2.0)",
                };
                options.ServerInstructions = Instructions;
            })
            .WithTools<MailTools>();
    }
}
