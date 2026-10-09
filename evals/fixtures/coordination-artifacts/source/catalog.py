class Catalog:
    def __init__(self):
        self.records = ()

    def refresh(self, records):
        candidate = tuple(records)
        self.records = candidate
        self._validate_unique(candidate)

    @staticmethod
    def _validate_unique(records):
        identifiers = [record["id"] for record in records]
        if len(identifiers) != len(set(identifiers)):
            raise ValueError("duplicate identifier")

    def read(self):
        return self.records
