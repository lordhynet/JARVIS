using Microsoft.AspNetCore.Hosting;

namespace Jarvis.Tests;

internal static class TestOwnerManagement
{
    public const string Key = "test-only-owner-management-secret-123456789";
    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("BudgetManagement:OwnerKey", Key);
        builder.UseSetting("BudgetManagement:SubjectId", "fixture-owner");
    }
    public static void Authorize(HttpClient client) => client.DefaultRequestHeaders.Add("X-Jarvis-Owner-Key", Key);
}
