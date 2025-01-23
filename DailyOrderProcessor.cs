using System.Text;
using DMS.Domain.Entities;
using DMS.Domain.Enums;
using DMS.Persistance.Context;
using Microsoft.EntityFrameworkCore;

namespace DMS.API.Infrastructure.Workers;

public class DailyOrderProcessor(IServiceProvider serviceProvider, ILogger<DailyOrderProcessor> logger)
    : BackgroundService
{
    private readonly ManualResetEvent _aggregationCompleted = new(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now.Hour == 0 && now.Minute == 0)
                {
                    logger.LogInformation("Starting daily order processing...");
                    await ProcessDailyOrders(stoppingToken);
                    logger.LogInformation("Daily order processing completed.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Check every minute
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during the daily processing loop.");
            }
        }
    }
    
    private async Task ProcessDailyOrders(CancellationToken stoppingToken)
    {
        var aggregationTask = Task.Run(() => AggregateDailyOrders(), stoppingToken);
        var synchronizationTask = Task.Run(() => SynchronizeOrdersWithExternalSystems(), stoppingToken);

        await Task.WhenAll(aggregationTask, synchronizationTask);

        logger.LogInformation("All daily tasks completed successfully.");
    }

    private void AggregateDailyOrders()
    {
        logger.LogInformation("Starting order aggregation...");

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DMSDBContext>(); // Replace with your DbContext

        var dailyOrders = dbContext.Orders
            .Where(o => o.CreatedAtUtc.Date == DateTime.UtcNow.Date)
            .Include(order => order.Items)
            .ToList();

        decimal totalRevenue = dailyOrders.SelectMany(o => o.Items).Sum(i => i.Quantity * i.Price);
        var ordersByStatus = dailyOrders.GroupBy(o => o.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        
        var aggregationResult = new StringBuilder();
        aggregationResult.AppendLine($"Total revenue: {totalRevenue}");
        foreach ((OrderStatus status, int count) in ordersByStatus)
            aggregationResult.AppendLine($"Orders with status {status}: {count}");
        File.WriteAllText("daily_orders_aggregation.txt", aggregationResult.ToString());
        
        logger.LogInformation("Order aggregation completed. Signaling synchronization...");
        _aggregationCompleted.Set();
    }

    private void SynchronizeOrdersWithExternalSystems()
    {
        logger.LogInformation("Waiting for aggregation to complete before starting synchronization...");

        _aggregationCompleted.WaitOne();

        logger.LogInformation("Starting order synchronization...");

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DMSDBContext>(); // Replace with your DbContext

        var completedOrders = dbContext.Orders
            .Where(o => o.Status == OrderStatus.Approved && o.CreatedAtUtc.Date == DateTime.UtcNow.Date)
            .ToList();

        Parallel.ForEach(completedOrders, new ParallelOptions
        {
            MaxDegreeOfParallelism = 10
        }, order =>
        {
            SyncWithERP(order);
            UpdateCustomerLoyaltyPoints(order);
            NotifyShippingPartner(order);
        });

        logger.LogInformation("Order synchronization completed.");
    }

    private void SyncWithERP(Order order)
    {
        logger.LogInformation("Order {OrderId} synced with ERP.", order.Id);
    }

    private void UpdateCustomerLoyaltyPoints(Order order)
    {
        logger.LogInformation("Loyalty points updated for Company {CompanyId}.", order.CompanyId);
    }

    private void NotifyShippingPartner(Order order)
    {
        logger.LogInformation("Shipping partner notified for Order {OrderId}.", order.Id);
    }
}
