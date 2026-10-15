using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zentriq.Application.Common;
using Zentriq.Application.Licensing;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.Setup;

namespace Zentriq.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers data access and core services. The application role's password comes from the
    /// protected app secrets file, or from ZENTRIQ_DB_APP_PASSWORD for development and CI.
    /// </summary>
    public static IServiceCollection AddZentriqInfrastructure(this IServiceCollection services, ZentriqOptions options, ZentriqPaths paths, string applicationName)
    {
        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretProtector>(_ => SecretProtectorFactory.Create());
        services.AddSingleton<SecretStore>();

        var appSecrets = new Lazy<AppSecrets>(() => LoadAppSecrets(paths));
        services.AddSingleton<Func<AppSecrets>>(_ => () => appSecrets.Value);

        services.AddSingleton(_ =>
        {
            var cs = ConnectionStrings.Build(options.Database, options.Database.AppUser, appSecrets.Value.DbAppPassword, applicationName: applicationName);
            return new NpgsqlDataSourceBuilder(cs).Build();
        });
        services.AddDbContext<ZentriqDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton(_ => EmbeddedPublicKeys.Load());
        services.AddSingleton<LicenseVerifier>();
        services.AddScoped<LicensingService>();
        services.AddScoped<InitializationService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<SqlMigrator>();
        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<Zentriq.Infrastructure.ActiveDirectory.DirectoryStore>();
        services.AddSingleton<Zentriq.Infrastructure.Mail.MailService>();
        services.AddSingleton<Zentriq.Infrastructure.Vulnerabilities.VulnStore>();
        services.AddSingleton<Zentriq.Infrastructure.Vulnerabilities.VulnFeedService>();
        services.AddSingleton<Zentriq.Infrastructure.Reports.ReportDelivery>();
        services.AddScoped<Zentriq.Infrastructure.Inventory.InventoryStore>();
        return services;
    }

    private static AppSecrets LoadAppSecrets(ZentriqPaths paths)
    {
        var envPassword = Environment.GetEnvironmentVariable("ZENTRIQ_DB_APP_PASSWORD");
        var envInstallSecret = Environment.GetEnvironmentVariable("ZENTRIQ_INSTALL_SECRET");
        if (!string.IsNullOrEmpty(envPassword) && !string.IsNullOrEmpty(envInstallSecret))
            return new AppSecrets { DbAppPassword = envPassword, InstallSecret = envInstallSecret };
        return new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile);
    }
}
