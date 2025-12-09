using System;
using System.Threading.Tasks;
using MAS_BT.Core;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Services
{
    /// <summary>
    /// Helper utilities for graceful shutdown tasks used by example runners.
    /// </summary>
    public static class ShutdownHelper
    {
        /// <summary>
        /// Shutdown the StorageMqttNotifier if present in the provided BTContext.
        /// The method swallows exceptions but logs them via the provided logger.
        /// </summary>
        public static async Task ShutdownStorageNotifierAsync(BTContext context, ILogger? logger = null, bool flushPending = true)
        {
            if (context == null) return;

            try
            {
                var storageNotifier = context.Get<StorageMqttNotifier>("StorageMqttNotifier");
                if (storageNotifier != null)
                {
                    logger?.LogInformation("ShutdownHelper: Shutting down StorageMqttNotifier (flushPending={FlushPending})", flushPending);
                    await storageNotifier.ShutdownAsync(flushPending);
                    logger?.LogInformation("ShutdownHelper: StorageMqttNotifier shutdown completed");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "ShutdownHelper: Error while shutting down StorageMqttNotifier");
            }
        }
    }
}
