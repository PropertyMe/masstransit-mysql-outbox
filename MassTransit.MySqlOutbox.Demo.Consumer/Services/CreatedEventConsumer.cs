using MassTransit.MySqlOutbox.Abstractions;
using MassTransit.MySqlOutbox.Demo.Consumer.Contexts;
using MassTransit.MySqlOutbox.Demo.Shared.Events;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;

namespace MassTransit.MySqlOutbox.Demo.Consumer.Services;

#pragma warning disable CS9113 // Parameter is unread.

public class CreatedEventConsumer(CreationConsumerContext dbContext, IConfiguration configuration, IServiceProvider sp, Options options)
   : InboxConsumer<EntityCreated, CreationConsumerContext>(sp)
#pragma warning restore CS9113 // Parameter is unread.
{
   protected override async Task Consume(EntityCreated message, IDbContextTransaction dbContextTransaction, CancellationToken ct)
   {
      var id = message.EntityId;
      Console.WriteLine($"*** [{options.ConsumerId}] *** DDDEntity with id {id} was created at {message.OccurredAt}. Event received at {DateTime.Now} from publisher {message.PublisherId}.");
      switch (DateTime.Now.Millisecond % 3)
      {
         // case 0:
         //    Console.WriteLine($" ------ >>>> Application Exception thrown for {id}");
         //    throw new ApplicationException("Exception throwing simulation");
         // case 1:
         //    Console.WriteLine($" ------ >>>> End of stream exception thrown for {id}");
         //    throw new EndOfStreamException();
         case 2:
            Console.WriteLine($" ------ >>>> Simulating a transient MySql exception for {id}");
            await SimulateMySql1042Async(configuration, ct);
            return;
         default:
            Console.WriteLine($" ------ >>>> Successful consumer behavior for {id}");
            return;
      }
   }

      private async Task SimulateMySql1042Async(IConfiguration configuration, CancellationToken ct)
   {
      // Derive a bad host connection string from the current one.
      var originalConnString = configuration.GetConnectionString("DefaultConnection");
      var badConnString = ReplaceHost(originalConnString!, "nonexistent-host-will-break");

      try
      {
         await using var conn = new MySqlConnection(badConnString);
         await conn.OpenAsync(ct); // Will throw
      }
      catch (MySqlException ex) when (ex.Number == 1042) //connection error
      {
         // Rethrow so EF execution strategy sees the genuine MySqlException
         throw;
      }
   }

   private static string ReplaceHost(string cs, string newHost)
   {
      // naive; but works for our test code
      return System.Text.RegularExpressions.Regex.Replace(
         cs,
         "(Server|Host)=[^;]+",
         $"Server={newHost}",
         System.Text.RegularExpressions.RegexOptions.IgnoreCase);
   }
}