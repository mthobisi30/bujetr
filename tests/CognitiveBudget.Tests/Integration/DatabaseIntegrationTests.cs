using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Configurations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CognitiveBudget.Web.Data;
using CognitiveBudget.Web.Models.Domain;

namespace CognitiveBudget.Tests.Integration
{
    // Note: these tests spin up a real PostgreSQL container. They are slightly
    // slower than typical unit tests, but provide confidence that our EF model
    // and migrations work end-to-end.
    public class DatabaseIntegrationTests : IAsyncLifetime
    {
        private readonly PostgreSqlTestcontainer _dbContainer;

        public DatabaseIntegrationTests()
        {
            _dbContainer = new TestcontainersBuilder<PostgreSqlTestcontainer>()
                .WithDatabase(new PostgreSqlTestcontainerConfiguration
                {
                    Database = "integration",
                    Username = "postgres",
                    Password = "postgres",
                })
                .WithCleanUp(true)
                .Build();
        }

        public async Task InitializeAsync()
        {
            await _dbContainer.StartAsync();
            // apply migrations programmatically against the container
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_dbContainer.ConnectionString)
                .Options;

            using var ctx = new ApplicationDbContext(options);
            await ctx.Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            await _dbContainer.StopAsync();
            await _dbContainer.DisposeAsync();
        }

        [Fact]
        public async Task CanCreateAndQueryUser()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_dbContainer.ConnectionString)
                .Options;

            using var ctx = new ApplicationDbContext(options);
            var user = new ApplicationUser { UserName = "test@local", Email = "test@local" };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();

            var saved = await ctx.Users.FirstOrDefaultAsync(u => u.UserName == "test@local");
            Assert.NotNull(saved);
        }
    }
}
