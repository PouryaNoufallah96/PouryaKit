namespace PouryaKit.Web;

/// <summary>
/// The peer Hosts whose requests may continue an Operation of theirs here. A request continues one only when its caller
/// is authenticated and the <see cref="PouryaKitWebOptions.ActorClaim"/> of an authenticated identity names a peer in
/// <see cref="Peers"/>: its Operation then runs with the Actor, tenant and correlation the Kit's headers carry, caused by
/// the peer's Operation. A header never identifies a peer, and every other caller's headers are ignored. Empty, the
/// default, trusts no one. A blank peer names no identity, so the Host does not start: <c>AddPouryaKitWeb</c> validates
/// the peers however the Host configures them, and the Host fails naming this option when it starts, or earlier, where
/// <c>MapPouryaKitModuleEndpoints</c> reads them.
/// </summary>
/// <remarks>
/// A peer's identity must be one no person can hold, such as a client-credentials token's subject or a client
/// certificate's thumbprint, as the application's authentication scheme issues it: a trusted peer speaks for any Actor.
/// </remarks>
public sealed class PouryaKitTrustedPeerOptions
{
    public ISet<string> Peers { get; } = new HashSet<string>(StringComparer.Ordinal);
}
