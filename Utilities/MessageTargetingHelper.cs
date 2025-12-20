using System;
using I40Sharp.Messaging.Models;

namespace MAS_BT.Utilities
{
    /// <summary>
    /// Helper class for determining message targeting based on I4.0 Frame Receiver information.
    /// Supports role-based broadcasts and explicit agent targeting.
    /// </summary>
    public static class MessageTargetingHelper
    {
        /// <summary>
        /// Determines if a message is targeted at a specific agent based on Receiver ID or Role.
        /// </summary>
        /// <param name="message">The I4.0 message to check</param>
        /// <param name="agentId">The ID of the current agent</param>
        /// <param name="agentRole">The role of the current agent (e.g., "ModuleHolon", "PlanningHolon")</param>
        /// <returns>True if the message is intended for this agent</returns>
        public static bool IsTargetedAtAgent(I40Message? message, string agentId, string agentRole)
        {
            if (message == null)
            {
                return false;
            }

            var receiver = message.Frame?.Receiver;
            
            // No receiver specified → Legacy mode, all agents process it
            if (receiver == null)
            {
                return true;
            }

            // Explicit agent ID targeting
            var receiverId = receiver.Identification?.Id;
            if (!string.IsNullOrWhiteSpace(receiverId))
            {
                // Check for exact match
                if (receiverId.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Check for "Broadcast" special value
                if (receiverId.Equals("Broadcast", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Workaround: Check if receiverId is like "P101_Planning" or "P101_PlanningHolon"
                // and agentId is "P101" (Dispatcher sometimes sends sub-holon IDs)
                if (receiverId.StartsWith(agentId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Role-based targeting (broadcast to all agents of a specific role)
            var receiverRole = receiver.Role?.Name;
            if (!string.IsNullOrWhiteSpace(receiverRole) 
                && receiverRole.Equals(agentRole, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Receiver specified but doesn't match → not for us
            return false;
        }

        /// <summary>
        /// Determines if a message should be forwarded to a child agent based on the child's role.
        /// </summary>
        /// <param name="message">The I4.0 message to check</param>
        /// <param name="childRole">The role of the child agent (e.g., "PlanningHolon", "ExecutionHolon")</param>
        /// <returns>True if the message should be forwarded to the child</returns>
        public static bool ShouldForwardToChild(I40Message? message, string childRole)
        {
            if (message == null)
            {
                return false;
            }

            var receiver = message.Frame?.Receiver;
            
            // No receiver info → don't auto-forward (legacy mode requires explicit forwarding logic)
            if (receiver == null)
            {
                return false;
            }

            // Message explicitly targets child role
            var receiverRole = receiver.Role?.Name;
            if (!string.IsNullOrWhiteSpace(receiverRole) 
                && receiverRole.Equals(childRole, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Message explicitly targets child ID (if provided)
            var receiverId = receiver.Identification?.Id;
            if (!string.IsNullOrWhiteSpace(receiverId) 
                && receiverId.Contains(childRole, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the message's receiver is a broadcast (no specific ID, only role).
        /// </summary>
        /// <param name="message">The I4.0 message to check</param>
        /// <returns>True if this is a role-based broadcast message</returns>
        public static bool IsBroadcastMessage(I40Message? message)
        {
            if (message == null)
            {
                return false;
            }

            var receiver = message.Frame?.Receiver;
            if (receiver == null)
            {
                return false;
            }

            var receiverId = receiver.Identification?.Id;
            var receiverRole = receiver.Role?.Name;

            // Broadcast if:
            // 1. Receiver ID is null/empty but Role is set (role-based broadcast)
            // 2. Receiver ID is explicitly "Broadcast"
            return (string.IsNullOrWhiteSpace(receiverId) && !string.IsNullOrWhiteSpace(receiverRole))
                   || string.Equals(receiverId, "Broadcast", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a clone of a message with a new receiver.
        /// Useful for forwarding messages to child agents with updated targeting.
        /// </summary>
        /// <param name="original">The original message to clone</param>
        /// <param name="receiverId">New receiver ID</param>
        /// <param name="receiverRole">New receiver role</param>
        /// <returns>Cloned message with updated receiver</returns>
        public static I40Message CloneWithNewReceiver(I40Message original, string receiverId, string receiverRole)
        {
            if (original == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            var cloned = new I40Message
            {
                Frame = new MessageFrame
                {
                    Sender = original.Frame?.Sender,
                    Receiver = new Participant
                    {
                        Identification = new Identification { Id = receiverId },
                        Role = new Role { Name = receiverRole }
                    },
                    Type = original.Frame?.Type,
                    ConversationId = original.Frame?.ConversationId,
                    ReplyBy = original.Frame?.ReplyBy
                },
                InteractionElements = original.InteractionElements
            };

            return cloned;
        }
    }
}
