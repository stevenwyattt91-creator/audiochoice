using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IInternalAuditStore
{
    InternalAccess? Access(Guid userID);
    bool ClaimInitialAdmin(Guid userID);
    IReadOnlyList<AuditAssignmentSummary> Dashboard(Guid userID, bool admin);
    AuditorEarnings Earnings(Guid userID);
    AuditWorkspace? Workspace(Guid assignmentID, Guid userID, bool admin);
    bool Claim(Guid assignmentID, Guid userID);
    AuditDecisionRecord? SaveDecision(Guid assignmentID, Guid candidateID, Guid userID, bool admin, AuditDecisionRequest request);
    bool Complete(Guid assignmentID, Guid userID, bool admin);
    IReadOnlyList<InternalAccess> Users();
    bool Grant(Guid actorID, Guid userID, string role);
    bool SetActive(Guid actorID, Guid userID, bool active);
    Guid? CreateAssignment(Guid actorID, CreateAuditAssignmentRequest request);
    Guid? CreateFocusedAssignmentForExploreCatalog(string catalogID);
    AdminDashboardSummary AdminDashboard();
    IReadOnlyList<AdminCatalogEditionSummary> Catalog(string? search);
    AdminCatalogEditionSummary? CatalogEdition(Guid editionID);
    IReadOnlyList<AdminAuditPayment> Payments();
    bool ApproveAssignment(Guid actorID, Guid assignmentID);
    bool SetAssignmentCompensation(Guid actorID, Guid assignmentID, decimal amount);
    bool RejectAssignment(Guid actorID, Guid assignmentID);
    bool MarkAssignmentPaid(Guid actorID, Guid assignmentID, string? note);
    bool CreateAutomaticFocusedAssignment(Guid scanJobID);
    IReadOnlyList<Guid> DeleteAllAssignments();
    AuditReviewSource? ReviewSource(Guid assignmentID);
    AuditReviewClip? ReviewClip(Guid assignmentID, Guid candidateID);
    bool SaveReviewSource(Guid actorID, Guid assignmentID, AuditReviewSource source);
    bool SaveReviewClip(Guid assignmentID, Guid candidateID, AuditReviewClip clip);
    bool ScheduleReviewMediaCleanup(Guid actorID, Guid assignmentID);
}

public sealed class DisabledInternalAuditStore : IInternalAuditStore
{
    public InternalAccess? Access(Guid userID) => null;
    public bool ClaimInitialAdmin(Guid userID) => false;
    public IReadOnlyList<AuditAssignmentSummary> Dashboard(Guid userID, bool admin) => [];
    public AuditorEarnings Earnings(Guid userID) => new(0, 0, 0, 0);
    public AuditWorkspace? Workspace(Guid assignmentID, Guid userID, bool admin) => null;
    public bool Claim(Guid assignmentID, Guid userID) => false;
    public AuditDecisionRecord? SaveDecision(Guid assignmentID, Guid candidateID, Guid userID, bool admin, AuditDecisionRequest request) => null;
    public bool Complete(Guid assignmentID, Guid userID, bool admin) => false;
    public IReadOnlyList<InternalAccess> Users() => [];
    public bool Grant(Guid actorID, Guid userID, string role) => false;
    public bool SetActive(Guid actorID, Guid userID, bool active) => false;
    public Guid? CreateAssignment(Guid actorID, CreateAuditAssignmentRequest request) => null;
    public Guid? CreateFocusedAssignmentForExploreCatalog(string catalogID) => null;
    public AdminDashboardSummary AdminDashboard() => new(0, 0, 0, 0, 0);
    public IReadOnlyList<AdminCatalogEditionSummary> Catalog(string? search) => [];
    public AdminCatalogEditionSummary? CatalogEdition(Guid editionID) => null;
    public IReadOnlyList<AdminAuditPayment> Payments() => [];
    public bool ApproveAssignment(Guid actorID, Guid assignmentID) => false;
    public bool SetAssignmentCompensation(Guid actorID, Guid assignmentID, decimal amount) => false;
    public bool RejectAssignment(Guid actorID, Guid assignmentID) => false;
    public bool MarkAssignmentPaid(Guid actorID, Guid assignmentID, string? note) => false;
    public bool CreateAutomaticFocusedAssignment(Guid scanJobID) => false;
    public IReadOnlyList<Guid> DeleteAllAssignments() => [];
    public AuditReviewSource? ReviewSource(Guid assignmentID) => null;
    public AuditReviewClip? ReviewClip(Guid assignmentID, Guid candidateID) => null;
    public bool SaveReviewSource(Guid actorID, Guid assignmentID, AuditReviewSource source) => false;
    public bool SaveReviewClip(Guid assignmentID, Guid candidateID, AuditReviewClip clip) => false;
    public bool ScheduleReviewMediaCleanup(Guid actorID, Guid assignmentID) => false;
}
