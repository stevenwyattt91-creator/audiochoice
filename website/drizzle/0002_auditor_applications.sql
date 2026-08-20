CREATE TABLE `auditor_applications` (
	`id` text PRIMARY KEY NOT NULL,
	`email` text NOT NULL,
	`name` text NOT NULL,
	`phone` text NOT NULL,
	`location` text NOT NULL,
	`country` text NOT NULL,
	`availability` text NOT NULL,
	`experience` text DEFAULT '[]' NOT NULL,
	`device` text NOT NULL,
	`headphones` text NOT NULL,
	`tax_residency` text NOT NULL,
	`notes` text DEFAULT '' NOT NULL,
	`age_confirmed_at` text NOT NULL,
	`sensitive_content_confirmed_at` text NOT NULL,
	`confidentiality_confirmed_at` text NOT NULL,
	`contractor_confirmed_at` text NOT NULL,
	`contact_consented_at` text NOT NULL,
	`status` text DEFAULT 'submitted' NOT NULL,
	`created_at` text NOT NULL,
	`updated_at` text NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `auditor_applications_email_unique` ON `auditor_applications` (`email`);
--> statement-breakpoint
CREATE INDEX `auditor_applications_status_idx` ON `auditor_applications` (`status`);
