import assert from "node:assert/strict";
import test from "node:test";
import { parseAuditComparison, parseFindingResolution, parseFindingReview, parseFixExecutionAccepted, parseFixExecutionStatus, parseFixPlanPreview, parseReauditAccepted } from "./remediation-contract.ts";
import { canCreateReaudit, comparisonPresentation, failureCategoryLabel, failureMessage, isTerminalExecution, nextPollDelay, resolutionPresentation, reviewPresentation, toggleSelection } from "./remediation-presentation.ts";

const id=(n:number)=>`00000000-0000-0000-0000-${String(n).padStart(12,"0")}`, now="2026-08-06T01:02:03Z";
const preview=()=>({auditId:id(1),sourceDocumentVersionId:id(2),sourceDocumentVersionSha256:"a".repeat(64),resolvedRuleSetHash:"b".repeat(64),documentKindSnapshot:"Skripsi",plannerVersion:"fix-plan-preview/1.0",selectedFindingCount:1,plannedFindingCount:1,unsupportedFindingCount:0,conflictFindingCount:0,invalidFindingCount:0,items:[{findingId:id(3),ruleCode:"PPKI-LAY-019",validationKey:"body.justified",ruleOrdinal:1,disposition:"Planned",diagnosticCode:"fix-plan-item-planned"}],operations:[{}],conflicts:[],planHash:"c".repeat(64),state:"Ready",diagnostics:[]});
const status=()=>({id:id(4),auditId:id(1),sourceDocumentVersionId:id(2),resultDocumentVersionId:null,planHash:"c".repeat(64),state:"Queued",plannedOperationCount:1,completedOperationCount:0,failedOperationCount:0,resultSha256:null,failureCategory:null,safeFailureCode:null,attemptCount:0,maxAttempts:3,retryPending:false,leaseState:"none",queuedAt:now,startedAt:null,completedAt:null});
const review=()=>({reviewCaseId:id(8),findingId:id(3),auditId:id(1),sourceDocumentVersionId:id(2),resolutionState:"Open",reviewState:"PendingReview",requestedDisposition:"Ignore",requestedByUserId:id(9),latestDecision:null,eventCount:1,latestEventAt:now,permissions:{canRequestReview:false,canReportManualRemediation:false,canDecide:true},allowedDecisions:["Ignore","NeedsRevision","Reject"],events:[{sequence:1,eventType:"ReviewRequested",requestedDisposition:"Ignore",decision:null,actorUserId:id(9),note:"teks <b>biasa</b>",createdAt:now}]});

test("canonical UUID is accepted without RFC variant restriction",()=>assert.equal(parseFixPlanPreview(preview()).auditId,id(1)));
test("preview counts operations without exposing payload",()=>assert.equal(parseFixPlanPreview(preview()).operationCount,1));
test("preview preserves exact finding identity",()=>assert.equal(parseFixPlanPreview(preview()).items[0].findingId,id(3)));
test("preview rejects unknown state",()=>assert.throws(()=>parseFixPlanPreview({...preview(),state:"Future"})));
test("preview rejects malformed hash",()=>assert.throws(()=>parseFixPlanPreview({...preview(),planHash:"bad"})));
test("preview rejects unknown disposition",()=>assert.throws(()=>parseFixPlanPreview({...preview(),items:[{...preview().items[0],disposition:"Maybe"}]})));
test("accepted execution stays canonical",()=>assert.equal(parseFixExecutionAccepted({id:id(4),auditId:id(1),sourceDocumentVersionId:id(2),planHash:"c".repeat(64),plannerVersion:"fix-plan-preview/1.0",state:"Queued",selectedFindingCount:1,plannedOperationCount:1,queuedAt:now,statusCode:"queued",statusMessage:"queued",replayed:false}).state,"Queued"));
for(const state of ["Queued","Processing","Completed","Failed","NoChange"] as const)test(`execution ${state} parses`,()=>assert.equal(parseFixExecutionStatus({...status(),state}).state,state));
test("attempt and retry fields parse",()=>{const r=parseFixExecutionStatus({...status(),attemptCount:2,retryPending:true});assert.deepEqual([r.attemptCount,r.maxAttempts,r.retryPending],[2,3,true])});
test("safeFailureCode maps to UI failureCode",()=>assert.equal(parseFixExecutionStatus({...status(),state:"Failed",failureCategory:"InvalidSource",safeFailureCode:"source-hash-mismatch"}).failureCode,"source-hash-mismatch"));
test("raw exception field is ignored",()=>assert.equal(parseFixExecutionStatus({...status(),exception:"stack secret"}).state,"Queued"));
test("unknown failure category fails closed",()=>assert.throws(()=>parseFixExecutionStatus({...status(),failureCategory:"Other"})));
test("re-audit response parses",()=>assert.equal(parseReauditAccepted({auditId:id(5),status:"Queued",sourceAuditId:id(1),sourceFixExecutionId:id(4),documentVersionId:id(6),profileVersionId:id(7),resolvedRuleSetHash:"d".repeat(64),documentKindSnapshot:"Skripsi",queuedAt:now,replayed:false}).auditId,id(5)));
test("comparison preserves server classification",()=>{const r=parseAuditComparison({sourceAuditId:id(1),resultAuditId:id(5),fixExecutionId:id(4),sourceDocumentVersionId:id(2),resultDocumentVersionId:id(6),comparisonState:"Completed",summary:{sourceFindingCount:1,resultFindingCount:0,stillDetectedCount:0,changedCount:0,noLongerDetectedCount:1,newlyDetectedCount:0},page:1,pageSize:100,totalCount:1,items:[{status:"NoLongerDetected",before:{id:id(3)},after:null,ruleCode:"R",validationKey:"v",domain:"d",element:"e",severity:"Error",ruleOrdinal:1,location:{}}]});assert.deepEqual([r.items[0].status,r.items[0].beforeFindingId],["NoLongerDetected",id(3)])});
test("resolution remains an independent typed state",()=>assert.equal(parseFindingResolution({findingId:id(3),auditId:id(1),currentState:"Applied",resolutionCaseId:id(10),sourceDocumentVersionId:id(2),resultDocumentVersionId:id(6),fixExecutionId:id(4),reAuditId:null,resultFindingId:null,comparisonStatus:null,eventCount:1,latestEventAt:now,events:[]}).currentState,"Applied"));
test("review and resolution states remain separate",()=>{const r=parseFindingReview(review());assert.deepEqual([r.reviewState,r.resolutionState],["PendingReview","Open"])});
test("review note remains plain text",()=>assert.equal(parseFindingReview(review()).events[0].note,"teks <b>biasa</b>"));
test("history must arrive ascending",()=>assert.throws(()=>parseFindingReview({...review(),events:[{...review().events[0],sequence:2},{...review().events[0],sequence:1}]})));
test("allowed decisions remain server supplied",()=>assert.deepEqual(parseFindingReview(review()).allowedDecisions,["Ignore","NeedsRevision","Reject"]));

const failureCases=[
["fix-source-version-superseded","versi yang lebih baru"],["fix-plan-stale","tidak berlaku"],["source-storage-object-missing","tidak tersedia"],["source-hash-mismatch","integritas"],["source-package-invalid","DOCX"],["approved-plan-invalid","tidak valid"],["fix-provider-version-unavailable","mesin perbaikan"],["storage-download-transient","gangguan sementara"],["storage-upload-transient","gangguan sementara"],["database-transient","gangguan sementara"],["worker-lease-lost","diambil alih"],["fix-result-object-conflict","bertentangan"],["database-finalization-terminal","difinalisasi"]
] as const;
for(const [code,phrase] of failureCases)test(`${code} has safe Indonesian copy`,()=>assert.match(failureMessage(code),new RegExp(phrase,"i")));
test("unknown failure code is generic",()=>assert.match(failureMessage("unknown"),/Eksekusi gagal/));
for(const category of ["Conflict","InvalidInput","InvalidSource","InvalidPlan","CapabilityUnavailable","TransientInfrastructure","TerminalInfrastructure"] as const)test(`${category} category has text`,()=>assert.ok(failureCategoryLabel(category).length>4));
for(const state of ["Completed","Failed","NoChange"] as const)test(`${state} is terminal`,()=>assert.equal(isTerminalExecution(state),true));
for(const state of ["Queued","Processing"] as const)test(`${state} is pollable`,()=>assert.equal(isTerminalExecution(state),false));
test("only Completed with result permits re-audit",()=>{assert.equal(canCreateReaudit("Completed",id(6)),true);assert.equal(canCreateReaudit("Failed",id(6)),false);assert.equal(canCreateReaudit("NoChange",null),false)});
test("poll delay is deterministic",()=>assert.deepEqual([0,1,2,3].map(nextPollDelay),[2000,4000,8000,15000]));
test("poll delay stays bounded",()=>assert.equal(nextPollDelay(50),15000));
test("eligible selection is added",()=>assert.deepEqual(toggleSelection([],id(1),true),[id(1)]));
test("ineligible selection is rejected",()=>assert.deepEqual(toggleSelection([],id(1),false),[]));
test("existing selection toggles off",()=>assert.deepEqual(toggleSelection([id(1)],id(1),false),[]));
test("selection is bounded at 100",()=>assert.equal(toggleSelection(Array.from({length:100},(_,i)=>id(i+1)),id(999),true).length,100));
for(const state of ["Open","Applied","ReauditPending","VerifiedResolved","VerifiedStillDetected"] as const)test(`${state} resolution has explanation`,()=>assert.ok(resolutionPresentation(state).explanation.length>10));
test("Applied is not verified",()=>assert.equal(resolutionPresentation("Applied").verified,false));
for(const state of ["Ignored","AcceptedRisk","ManualRemediationReported"] as const)test(`${state} is not verified resolution`,()=>assert.match(reviewPresentation(state).explanation,/bukan bukti/));
for(const state of ["StillDetected","Changed","NoLongerDetected","NewlyDetected"] as const)test(`${state} comparison has group text`,()=>assert.ok(comparisonPresentation(state).length>5));
