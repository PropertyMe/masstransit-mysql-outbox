using System.Data;
using MassTransit.MySqlOutbox.Entities;
using MassTransit.MySqlOutbox.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MassTransit.MySqlOutbox.Abstractions;

public abstract class InboxConsumer<TMessage, TDbContext> : IConsumer<TMessage>
   where TMessage : class
   where TDbContext : DbContext, IInboxDbContext
{
   private readonly string _consumerId;
   private readonly IServiceProvider _sp;

   protected InboxConsumer(IServiceProvider sp)
   {
      _consumerId = GetType()
         .ToString();
      _sp = sp;
   }

   public async Task Consume(ConsumeContext<TMessage> context)
   {
      var messageId = context.Headers.Get<Guid>(Constants.OutboxMessageId) ?? context.MessageId;
      var dbContext = _sp.GetRequiredService<TDbContext>();
      var logger = _sp.GetRequiredService<ILogger<InboxConsumer<TMessage, TDbContext>>>();
      var ct = context.CancellationToken;

      var executionStrategy = dbContext.Database.CreateExecutionStrategy();

      if (!executionStrategy.RetriesOnFailure)
      {
         //no retry, so just go ahgead and process the message
         await ProcessInboxMessageAsync(dbContext, logger, messageId, context, ct);
         return;
      }

      //We're going to retry, so we need to retry according to the executionStrategy
      await executionStrategy.ExecuteAsync(async () =>
      {
         await ProcessInboxMessageAsync(dbContext, logger, messageId, context, ct);
      });

   }

      private async Task ProcessInboxMessageAsync(
      TDbContext dbContext,
      ILogger logger,
      Guid? messageId,
      ConsumeContext<TMessage> context,
      CancellationToken ct)
   {
      // Ensure (MessageId, ConsumerId) uniqueness is enforced at DB level to avoid races.
      var exists = await dbContext.InboxMessages
         .AnyAsync(x => x.MessageId == messageId && x.ConsumerId == _consumerId, ct);

      if (!exists)
      {
         dbContext.InboxMessages.Add(new InboxMessage
         {
            MessageId = messageId!.Value,
            CreatedAt = DateTime.UtcNow,
            State = MessageState.New,
            ConsumerId = _consumerId
         });
         await dbContext.SaveChangesAsync(ct);
      }

      await using var transaction =
         await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

      var inboxMessage = await dbContext.InboxMessages
         .FromSqlInterpolated($@"
               SELECT * FROM InboxMessages
               WHERE MessageId = {messageId}
               AND ConsumerId = {_consumerId}
               AND State = {MessageState.New}
               FOR UPDATE SKIP LOCKED")
         .OrderBy(x => x.MessageId)
         .FirstOrDefaultAsync(ct);

      if (inboxMessage == null)
      {
         await transaction.RollbackAsync(ct);
         return;
      }

      try
      {
         await Consume(context.Message, transaction, ct);

         inboxMessage.State = MessageState.Done;
         inboxMessage.UpdatedAt = DateTime.UtcNow;
         await dbContext.SaveChangesAsync(ct);

         await transaction.CommitAsync(ct);
      }
      catch (Exception ex)
      {
         logger.LogError(ex, "Exception while consuming message {messageId} by {consumerId}", messageId, _consumerId);

         try
         {
            await transaction.RollbackAsync(ct);
         }
         catch (Exception rollbackEx)
         {
            logger.LogWarning(rollbackEx, "Rollback failed for message {messageId}", messageId);
         }

         inboxMessage.UpdatedAt = DateTime.UtcNow;
         await dbContext.SaveChangesAsync(ct);
         throw;
      }
   }

   protected abstract Task Consume(TMessage message, IDbContextTransaction transactionScope, CancellationToken ct);
}