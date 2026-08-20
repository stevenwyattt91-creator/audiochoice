CREATE TABLE `beta_submission_limits` (
	`client_hash` text NOT NULL,
	`window_start` text NOT NULL,
	`attempts` integer DEFAULT 0 NOT NULL,
	PRIMARY KEY(`client_hash`, `window_start`)
);
--> statement-breakpoint
ALTER TABLE `beta_applicants` ADD `consented_at` text DEFAULT '' NOT NULL;
