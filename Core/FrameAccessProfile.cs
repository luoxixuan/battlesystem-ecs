#nullable enable
using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    public readonly struct FrameBindingId : IEquatable<FrameBindingId>
    {
        public string Value { get; }
        public FrameBindingId(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
        public bool Equals(FrameBindingId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is FrameBindingId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
        public override string ToString() => Value ?? string.Empty;
        public static implicit operator FrameBindingId(string value) => new FrameBindingId(value);
    }

    public readonly struct FrameAccessOwner : IEquatable<FrameAccessOwner>
    {
        public string Value { get; }
        public FrameAccessOwner(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
        public bool Equals(FrameAccessOwner other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is FrameAccessOwner other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
        public override string ToString() => Value ?? string.Empty;
        public static implicit operator FrameAccessOwner(string value) => new FrameAccessOwner(value);
    }

    public readonly struct FrameAccessReviewId : IEquatable<FrameAccessReviewId>
    {
        public string Value { get; }
        public FrameAccessReviewId(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
        public bool Equals(FrameAccessReviewId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is FrameAccessReviewId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
        public override string ToString() => Value ?? string.Empty;
        public static implicit operator FrameAccessReviewId(string value) => new FrameAccessReviewId(value);
    }

    public enum FrameAccessEvidence
    {
        Unreviewed = 0,
        SourceReviewed = 1,
        DisabledUnsafe = 2
    }

    public enum FrameAccessReviewDisposition
    {
        AcceptedCorrection,
        JustifiedException,
        DisabledUnsafe,
        SplitNode
    }

    public sealed class FrameAccessReviewRecord
    {
        public FrameAccessReviewId Id { get; }
        public string ArtifactId { get; }
        public string ArtifactSha256 { get; }
        public string EvidenceLocator { get; }
        public string TransitiveCallees { get; }
        public string MetadataFingerprint { get; }
        public string ParallelModel { get; }
        public FrameAccessReviewDisposition Disposition { get; }
        public bool IsApproved { get; }

        internal FrameAccessReviewRecord(FrameAccessReviewId id,string artifactId,string artifactSha256,
            string evidenceLocator,string transitiveCallees,string metadataFingerprint,string parallelModel,
            FrameAccessReviewDisposition disposition,bool isApproved)
        {Id=id;ArtifactId=artifactId;ArtifactSha256=artifactSha256;EvidenceLocator=evidenceLocator;TransitiveCallees=transitiveCallees;MetadataFingerprint=metadataFingerprint;ParallelModel=parallelModel;Disposition=disposition;IsApproved=isApproved;}
    }

    public sealed class FrameAccessProfile
    {
        public FrameBindingId BindingId { get; }
        public FrameAccessOwner Owner { get; }
        public FrameAccessEvidence Evidence { get; }
        public FrameAccessReviewId ReviewId { get; }
        public FrameAccessReviewRecord? Review { get; }
        public IReadOnlyList<FrameResource> Reads { get; }
        public IReadOnlyList<FrameResource> Writes { get; }
        public bool RequiresSystemBinding { get; }

        internal FrameAccessProfile(FrameBindingId bindingId, FrameAccessOwner owner,
            FrameAccessEvidence evidence, FrameAccessReviewId reviewId, FrameAccessReviewRecord? review, IReadOnlyList<FrameResource> reads,
            IReadOnlyList<FrameResource> writes, bool requiresSystemBinding)
        {
            BindingId = bindingId;
            Owner = owner;
            Evidence = evidence;
            ReviewId = reviewId;
            Review = review;
            Reads = reads;
            Writes = writes;
            RequiresSystemBinding = requiresSystemBinding;
        }
    }
}
