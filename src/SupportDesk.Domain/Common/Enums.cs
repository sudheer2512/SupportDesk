namespace SupportDesk.Domain;

/// <summary>Where a ticket or message came from / goes through.</summary>
public enum Channel
{
    Email, Portal, WebForm, Chat, WhatsApp, Phone, Sms, Facebook, Instagram, MobileApp, Api, WalkIn, Erp, Internal
}

public enum TicketPriority { Low = 1, Medium = 2, High = 3, Critical = 4 }

public enum TicketSeverity { Cosmetic = 1, Minor = 2, Major = 3, Critical = 4 }

/// <summary>
/// Fixed lifecycle categories. Tenants configure any number of statuses; each maps to one category.
/// Everything before Resolved counts as "open" for SLA and backlog metrics.
/// </summary>
public enum StatusCategory { Open = 1, Assigned = 2, InProgress = 3, Waiting = 4, Resolved = 5, Closed = 6 }

public enum ActorType { User, Contact, System, Automation, Ai, ApiClient, Platform, Integration }

public enum Visibility { Public, Internal }

public enum MessageDirection { Inbound, Outbound, Internal }

public enum SlaMilestoneKind { FirstResponse, NextResponse, Resolution }

/// <summary>Delivery state of an outbound webhook call or notification.</summary>
public enum DeliveryStatus { Pending, Succeeded, Failed, DeadLetter }

public enum EntityKind
{
    Ticket, Contact, Customer, Article, Agent, User, Contract, Installation, Product, Rule, Workflow, Conversation, Message, Topic, Tenant, Setting
}

public static class StatusCategoryExtensions
{
    public static bool IsOpen(this StatusCategory c) => c < StatusCategory.Resolved;
    public static bool IsResolvedOrClosed(this StatusCategory c) => c >= StatusCategory.Resolved;
}
