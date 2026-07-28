using BarberShopAPI.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarberShopAPI.Tests.Infrastructure
{
    [Collection(DatabaseCollection.Name)]
    /* this tells xUnit: "This test class belongs to the shared database collection", so it makes 
     * all tests using this base class run sequentially*/
    public abstract class IntegrationTestBase : IAsyncLifetime
    {
        protected readonly DatabaseFixture Fixture;
        protected readonly HttpClient Client;
        /* The above line of code lets test call your API.
         * Ex: var response = await client.GetAsync("/api/bookings")*/

        protected ApiFactory Factory => Fixture.Factory;
        /* instead of writing Fixture.Factory you write Factory everywhere */

        protected IntegrationTestBase(DatabaseFixture fixture)
        {
            Fixture = fixture;
            Client = fixture.Factory.CreateClient();
        }
        /* This is a base class that all your integration test classes inherit from. Its purpise is to avoid repeating
         * the same setup code in every test class*/

        // A fresh database per test: arrange-act-assert never inherits another test's rows.
        public Task InitializeAsync() => Fixture.ResetAsync();
        /* The above runs before every individual test.
         * So before a test starts, Respawn clears database, shopSettings reset, Hangfire reset, Fake emails cleared*/
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>A short-lived context for arranging or asserting, separate from the one the request used.</summary>
        protected BarberShopContext NewDb() => Factory.CreateDbContext();
        /* this lets you do this: using var db = NewDb();
         * db.Bookings.Add(...)
         await db.SaveChangesAsync()*/

        protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            // The API serialises enums as strings (see AddJsonOptions in Program.cs).
            Converters = { new JsonStringEnumConverter() }
        };
        /* This converts the enums into strings rather than numbers:
         * {
    "status": "Cancelled"
            }     
        Insetad of:
            {
        "status": 2
            }*/

        protected static StringContent Body(object payload) =>
            new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        /* instead of wriring this every test:
         * var json = JsonSerializer.Serialize(request);

            var content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");
        You simple write Body(request)*/

        /* Normally youd need var text = await response.Context.ReadAsStringasync();
         * var json = JsonDocument.Parse(Text);
         Instead you can write var json = await ReadJson(response);
        Then you can inspect it:
        Assert.Equal("Cancelled", json.GetProperty("status").GetString())*/
        protected static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            var raw = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
    }
}
