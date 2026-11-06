using Atm.Business.Common;
using Atm.Business.Security;
using Atm.Business.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atm.Business;

public static class DependencyInjection
{
    public static IServiceCollection AddBusiness(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AtmOptions>()
            .Bind(configuration.GetSection(AtmOptions.SectionName))
            .Validate(o => o.QuoteTimeToLiveSeconds > 0, "Atm:QuoteTimeToLiveSeconds must be positive.")
            .Validate(o => Enum.GetValues<Currency>().All(c => o.DailyLimits.TryGetValue(c, out var l) && l > 0),
                "Atm:DailyLimits must define a positive limit for TRY, USD and EUR.")
            .ValidateOnStart();

        services.AddOptions<CardHashingOptions>()
            .Bind(configuration.GetSection(CardHashingOptions.SectionName))
            .Validate(o => o.Secret.Length >= 16, "CardHashing:Secret must be at least 16 characters.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICardHasher, HmacCardHasher>();
        services.AddSingleton<IBusinessCalendar, BusinessCalendar>();

        services.AddScoped<AtmTransactionRunner>();
        services.AddScoped<QuoteReservationManager>();
        services.AddScoped<AtmService>();
        services.AddScoped<WithdrawalService>();
        services.AddScoped<DepositService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ConsistencyService>();
        services.AddScoped<ExchangeRateService>();
        services.AddScoped<OutboxQueryService>();
        return services;
    }
}
