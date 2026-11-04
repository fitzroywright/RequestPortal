namespace RequestPortal.Application.Messages;

public sealed class MessageThreadNotFoundException() : KeyNotFoundException("The conversation was not found or is unavailable.");
