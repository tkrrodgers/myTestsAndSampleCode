using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Every ReferenceUrl below was verified to return HTTP 200 before being added. They are fetched at
// judging time and become the judge's ground truth — so a URL that rots shows up as a visible retrieval
// failure rather than as a silent fabrication.
public static class ComparisonCatalog
{
    public static IReadOnlyList<ComparisonTopic> Topics { get; } =
    [
        new(
            "gke-autoscaling",
            "GCP · GKE autoscaling",
            "Explain how autoscaling works in Google Kubernetes Engine (GKE), including the main autoscaler types and when to use each.",
            [
                "https://cloud.google.com/kubernetes-engine/docs/concepts/cluster-autoscaler",
                "https://cloud.google.com/kubernetes-engine/docs/concepts/horizontalpodautoscaler",
                "https://cloud.google.com/kubernetes-engine/docs/concepts/verticalpodautoscaler"
            ],
            ["Horizontal Pod Autoscaler", "HPA", "Vertical Pod Autoscaler", "VPA", "Cluster Autoscaler", "node pool", "replicas", "metrics"]),
        new(
            "cloud-run-vs-functions",
            "GCP · Cloud Run vs Cloud Functions",
            "When should I choose Cloud Run instead of Cloud Functions on Google Cloud, and what are the key trade-offs?",
            [
                "https://cloud.google.com/run/docs/overview/what-is-cloud-run",
                "https://cloud.google.com/run/docs/about-instance-autoscaling",
                "https://cloud.google.com/functions/docs/concepts/overview"
            ],
            ["Cloud Run", "Cloud Functions", "container", "concurrency", "cold start", "event-driven", "scale to zero", "request"]),
        new(
            "iam-fundamentals",
            "GCP · IAM fundamentals",
            "Explain the difference between roles, permissions, policies, principals, and service accounts in Google Cloud IAM, and how they combine to grant access.",
            [
                "https://cloud.google.com/iam/docs/overview",
                "https://cloud.google.com/iam/docs/roles-overview",
                "https://cloud.google.com/iam/docs/service-account-overview"
            ],
            ["role", "permission", "policy", "binding", "principal", "service account", "least privilege", "predefined", "custom role"]),
        new(
            "llmops",
            "GCP · LLMOps",
            "What is LLMOps, and what are its core practices for running large language models in production?",
            [
                // cloud.google.com/discover/what-is-llmops is a marketing page with no article element,
                // so it yields navigation chrome rather than prose. Use the engineering docs instead.
                "https://cloud.google.com/architecture/mlops-continuous-delivery-and-automation-pipelines-in-machine-learning",
                "https://cloud.google.com/vertex-ai/generative-ai/docs/learn/overview"
            ],
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
