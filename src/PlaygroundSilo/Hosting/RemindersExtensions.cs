#warning return here

using System;
using PlaygroundSilo.Configuration;

namespace PlaygroundSilo.Hosting;

public static class RemindersExtensions
{
	// public static RedisReminderTableOptions Apply(
	// 	this RedisReminderTableOptions redisOptions, SiloConfig? config)
	// {
	// 	var endpoint = config?.Reminders?.NatsEndpoint ?? ConfigDefaults.DefaultNatsUri;
	// 	(redisOptions.ConfigurationOptions ??= new()).ApplyUri(endpoint);
	// 	return redisOptions;
	// }

	public static ReminderOptions Apply(
		this ReminderOptions reminderOptions, SiloConfig? config)
	{
		reminderOptions.MinimumReminderPeriod = TimeSpan.Zero;
		return reminderOptions;
	}
}
