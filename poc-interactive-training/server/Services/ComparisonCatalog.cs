using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Reference sources drawn from CompareLLMoutput.md. Keywords form the programmatic
// "must-include" checklist used for keyword-coverage scoring and to ground the judge.
public static class ComparisonCatalog
{
    public static IReadOnlyList<ComparisonTopic> Topics { get; } =
    [
        new(
            "gke-autoscaling",
            "GCP · GKE autoscaling",
            "Explain how autoscaling works in Google Kubernetes Engine (GKE), including the main autoscaler types and when to use each.",
            ["https://docs.cloud.google.com/docs"],
            ["Horizontal Pod Autoscaler", "HPA", "Vertical Pod Autoscaler", "VPA", "Cluster Autoscaler", "node pool", "replicas", "metrics"]),
        new(
            "cloud-run-vs-functions",
            "GCP · Cloud Run vs Cloud Functions",
            "When should I choose Cloud Run instead of Cloud Functions on Google Cloud, and what are the key trade-offs?",
            ["https://docs.cloud.google.com/docs"],
            ["Cloud Run", "Cloud Functions", "container", "concurrency", "cold start", "event-driven", "scale to zero", "request"]),
        new(
            "iam-fundamentals",
            "GCP · IAM fundamentals",
            "Explain the difference between roles, permissions, policies, principals, and service accounts in Google Cloud IAM, and how they combine to grant access.",
            ["https://docs.cloud.google.com/docs"],
            ["role", "permission", "policy", "binding", "principal", "service account", "least privilege", "predefined", "custom role"]),
        new(
            "llmops",
            "GCP · LLMOps",
            "What is LLMOps, and what are its core practices for running large language models in production?",
            ["https://cloud.google.com/discover/what-is-llmops"],
            ["LLMOps", "prompt", "evaluation", "monitoring", "deployment", "fine-tuning", "governance", "observability", "data"]),
        new(
            "model-context-protocol",
            "GCP · Model Context Protocol",
            "What is the Model Context Protocol (MCP), and how do external knowledge sources integrate with it?",
            ["https://developers.google.com/knowledge/mcp"],
            ["Model Context Protocol", "MCP", "server", "client", "tools", "resources", "context", "knowledge"])
    ];

    public static ComparisonTopic? Find(string id) =>
        Topics.FirstOrDefault(topic => string.Equals(topic.Id, id, StringComparison.Ordinal));
}
