import { integer, primaryKey, sqliteTable, text } from "drizzle-orm/sqlite-core";

export const betaApplicants = sqliteTable("beta_applicants", {
  id: text("id").primaryKey(),
  email: text("email").notNull().unique(),
  name: text("name").notNull(),
  ownershipSource: text("ownership_source").notNull(),
  audiobooksPerMonth: text("audiobooks_per_month").notNull(),
  groupName: text("group_name").notNull().default("android_beta"),
  consentedAt: text("consented_at").notNull().default(""),
  createdAt: text("created_at").notNull(),
  updatedAt: text("updated_at").notNull(),
});

export const betaSubmissionLimits = sqliteTable("beta_submission_limits", {
  clientHash: text("client_hash").notNull(),
  windowStart: text("window_start").notNull(),
  attempts: integer("attempts").notNull().default(0),
}, table => [primaryKey({ columns: [table.clientHash, table.windowStart] })]);

export const auditorApplications = sqliteTable("auditor_applications", {
  id: text("id").primaryKey(),
  email: text("email").notNull().unique(),
  name: text("name").notNull(),
  phone: text("phone").notNull(),
  location: text("location").notNull(),
  country: text("country").notNull(),
  availability: text("availability").notNull(),
  experience: text("experience").notNull().default("[]"),
  device: text("device").notNull(),
  headphones: text("headphones").notNull(),
  taxResidency: text("tax_residency").notNull(),
  notes: text("notes").notNull().default(""),
  ageConfirmedAt: text("age_confirmed_at").notNull(),
  sensitiveContentConfirmedAt: text("sensitive_content_confirmed_at").notNull(),
  confidentialityConfirmedAt: text("confidentiality_confirmed_at").notNull(),
  contractorConfirmedAt: text("contractor_confirmed_at").notNull(),
  contactConsentedAt: text("contact_consented_at").notNull(),
  status: text("status").notNull().default("submitted"),
  createdAt: text("created_at").notNull(),
  updatedAt: text("updated_at").notNull(),
});
