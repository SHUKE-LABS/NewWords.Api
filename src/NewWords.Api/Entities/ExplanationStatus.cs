namespace NewWords.Api.Entities
{
    /// <summary>
    /// Lifecycle state of a <see cref="WordExplanation"/> row.
    /// Serialized numerically in API responses (0 = Ready, 1 = Pending).
    /// </summary>
    public enum ExplanationStatus
    {
        /// <summary>Explanation markdown is real and complete.</summary>
        Ready = 0,

        /// <summary>Placeholder row awaiting an LLM-generated explanation (all agents failed at add time).</summary>
        Pending = 1,
    }
}
