CREATE TABLE `beta_applicants` (
	`id` text PRIMARY KEY NOT NULL,
	`email` text NOT NULL,
	`name` text NOT NULL,
	`ownership_source` text NOT NULL,
	`audiobooks_per_month` text NOT NULL,
	`group_name` text DEFAULT 'android_beta' NOT NULL,
	`created_at` text NOT NULL,
	`updated_at` text NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `beta_applicants_email_unique` ON `beta_applicants` (`email`);