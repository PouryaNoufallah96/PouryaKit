"""The composition proof's own parts: the token both Hosts verify, the solutions it takes from a kept validation, its PASS line."""
import base64
import hashlib
import hmac
import json
from pathlib import Path
import tempfile
import unittest

from composition_proof import ISSUER, Generated, generated, pass_line, token


def decoded(part):
    return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))


class TokenTests(unittest.TestCase):
    def test_the_token_is_an_hs256_jws_over_the_issuer_audience_and_tenant_each_host_is_given(self) -> None:
        key = bytes(range(32))
        header, payload, signature = token(key, 1000).split(".")
        self.assertEqual({"alg": "HS256", "typ": "JWT"}, decoded(header))
        self.assertEqual({"iss": ISSUER, "aud": ISSUER, "sub": "composition-proof", "tenant": "north", "nbf": 1000, "exp": 1600},
                         decoded(payload))
        # RFC 7515: the signature is the unpadded base64url HMAC-SHA256 of the two encoded parts, under the Hosts' key.
        expected = hmac.new(key, f"{header}.{payload}".encode(), hashlib.sha256).digest()
        self.assertEqual(base64.urlsafe_b64encode(expected).rstrip(b"=").decode(), signature)


class GeneratedTests(unittest.TestCase):
    """The proof runs what the inventory's template consumers generated, from the folders a kept validation leaves."""

    def test_the_modular_monolith_and_the_service_are_taken_from_their_template_consumers_publishes(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder)
            for consumer, name in (("template-modular-monolith", "acme-shop"), ("template-service", "Acme.Stock")):
                (output / "consumers" / consumer / "publish").mkdir(parents=True)
                (output / "consumers" / consumer / "publish" / f"{name}.Host.dll").write_bytes(b"")
            modular, service = generated(output)
        self.assertEqual(("pouryakit-modular-monolith", "acme-shop", ("Catalog", "Billing", "Inventory")),
                         (modular.template, modular.name, modular.modules))
        self.assertEqual(("pouryakit-service", "Acme.Stock", ("Inventory",)), (service.template, service.name, service.modules))
        self.assertEqual(output / "consumers/template-service/Acme.Stock", service.solution)

    def test_an_output_without_the_kept_publishes_is_refused(self) -> None:
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(ValueError, "--keep"):
            generated(Path(folder))

    def test_the_queue_is_the_name_the_templates_service_name_symbol_derives(self) -> None:
        """templates.md: `acme-shop` for -n Acme.Shop, acme-shop and acme_shop, `my-shop` for My Shop."""
        names = ["Acme.Shop", "acme-shop", "acme_shop", "My Shop", ".Acme.Stock."]
        self.assertEqual(["acme-shop", "acme-shop", "acme-shop", "my-shop", "acme-stock"],
                         [Generated("", name, Path(), ()).queue for name in names])


class PassLineTests(unittest.TestCase):
    def test_the_pass_line_names_both_generated_solutions_the_queues_and_the_stock_item_observed(self) -> None:
        modular = Generated("pouryakit-modular-monolith", "acme-shop", Path(), ())
        service = Generated("pouryakit-service", "Acme.Stock", Path(), ())
        body = '{"productId":"p-1","quantityOnHand":0}'
        self.assertEqual(
            "PASS composition proof: product p-1, created in tenant north through acme-shop (pouryakit-modular-monolith), "
            "reached Acme.Stock (pouryakit-service) over one RabbitMQ holding the queues acme-shop and acme-stock, each Host on its "
            'own database of one PostgreSQL; the service answered GET /inventory/v2/stock-items/p-1 200 {"productId":"p-1",'
            '"quantityOnHand":0} 0.6 s after the create',
            pass_line(modular, service, "p-1", 0.64, body, ["acme-shop", "acme-stock"]))


if __name__ == "__main__":
    unittest.main()
