import unittest
from collections import defaultdict

from audit_gor_quests import in_gor_unit, select_roots, quest_issues


class GorQuestScopeTests(unittest.TestCase):
    def test_world_unit_bounds_exclude_other_maps_and_neighboring_regions(self):
        self.assertTrue(in_gor_unit(dict(map_id=0, x=3663, z=557)))
        for point in (dict(map_id=911, x=3663, z=557), dict(map_id=0, x=4096, z=557), dict(map_id=0, x=3663, z=1024)):
            self.assertFalse(in_gor_unit(point))

    def test_gor_giver_keeps_outgoing_quest_in_scope(self):
        ref = {1: dict(bType=8)}
        placement = {10: [dict(bCountryID=3, source_file='tcd/tnpc00000003.tcd', wMapID=0, fPosX=3663, fPosZ=557)]}
        _, roots = select_roots(ref, {1: [dict(npc_id=10, map_id=0, x=2500, z=4500)]}, placement, [])
        self.assertIn(1, roots)

    def test_gor_country_npc_outside_tebekut_is_included(self):
        placement = {10: [dict(bCountryID=2, source_file='tcd/tnpc00000402.tcd', wMapID=0, fPosX=2500, fPosZ=4500)]}
        _, roots = select_roots({1: dict(bType=8)}, {1: [dict(npc_id=10, map_id=0, x=2500, z=4500)]}, placement, [])
        self.assertIn(1, roots)

    def test_neutral_npc_in_another_region_does_not_expand_scope(self):
        placement = {10: [dict(bCountryID=3, source_file='tcd/tnpc00000402.tcd', wMapID=0, fPosX=2500, fPosZ=4500)]}
        _, roots = select_roots({1: dict(bType=8)}, {1: [dict(npc_id=10, map_id=0, x=2500, z=4500)]}, placement, [])
        self.assertNotIn(1, roots)

    def test_publisher_tebekut_recipes_are_not_lost_when_navigation_is_missing(self):
        _, roots = select_roots({1: dict(bType=18)}, {}, {}, [dict(id=1, cells=['Recipe', '1', 'Tebekut'])])
        self.assertIn(1, roots)

    def test_publisher_only_id_is_reported_without_inventing_a_client_identity(self):
        _, roots = select_roots({}, {}, {}, [dict(id=99, cells=['Mission', '1', 'Tebekut'])])
        self.assertEqual({'publisher_tebekut_catalogue'}, roots[99])

    def reward_contract_issues(self, kind, method):
        reference={1:dict(bType=18,unknown_prefix=0,terms=[],rewards=[dict(bRewardType=kind,bTakeMethod=method,dwRewardID=167)])}
        return quest_issues(1,reference,{}, {},defaultdict(list),defaultdict(list),defaultdict(list),defaultdict(list),{})

    def test_default_soul_reward_is_now_a_supported_contract(self):
        self.assertNotIn('reference_reward_contract_not_implemented',self.reward_contract_issues(9,1))

    def test_unverified_soul_reward_method_stays_reported(self):
        self.assertIn('reference_reward_contract_not_implemented',self.reward_contract_issues(9,2))

    def test_default_achievement_point_reward_is_supported(self):
        self.assertNotIn('reference_reward_contract_not_implemented',self.reward_contract_issues(10,1))

    def test_unverified_point_reward_method_stays_reported(self):
        self.assertIn('reference_reward_contract_not_implemented',self.reward_contract_issues(10,2))


if __name__ == '__main__':
    unittest.main()
