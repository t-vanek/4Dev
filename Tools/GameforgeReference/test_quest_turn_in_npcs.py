import copy
import unittest

from audit_quest_turn_in_npcs import analyze, add_npc_evidence


class TurnInAuditTests(unittest.TestCase):
    def setUp(self):
        self.tables = {'TQUESTCHART': [dict(dwQuestID=100, bType=8), dict(dwQuestID=101, dwParentID=100, bType=7,
                       bTriggerType=3, dwTriggerID=1, bConditionCheck=0)],
                       'TQUESTTERMCHART': [dict(dwQuestID=101, bTermType=1, dwTermID=100, bCount=0)],
                       'TQCONDITIONCHART': [dict(dwQuestID=101, bConditionType=3, dwConditionID=100, bCount=0)],
                       'TNPCCHART': [dict(wID=2, wMapID=0, bCountryID=0)]}
        self.ref = {100: dict(dwQuestID=100, bType=8, title='Fixture mission', terms=[]),
                    101: dict(dwQuestID=101, bType=7, unknown_prefix=100, terms=[dict(bTermType=1, dwTermID=100, bCount=0)])}
        self.nav = {100: [dict(type=20, npc_id=2, map_id=0)]}

    def status(self):
        return analyze(self.tables, self.ref, self.nav)['rows'][0]['status']

    def test_unique_matching_graph_produces_the_repair(self):
        r = analyze(self.tables, self.ref, self.nav)['rows'][0]
        self.assertEqual('verified_npc_mismatch', r['status'])
        self.assertEqual((1, 2), (r['current_npc'], r['new_npc']))

    def test_already_matching_npc_is_not_a_repair(self):
        self.tables['TQUESTCHART'][1]['dwTriggerID'] = 2
        self.assertEqual('npc_aligned', self.status())

    def test_multiple_targets_are_never_collapsed(self):
        self.nav[100].append(dict(type=20, npc_id=3, map_id=0))
        self.assertEqual('ambiguous_reference_npc', self.status())

    def test_missing_navigation_is_not_guessed_from_the_giver(self):
        self.nav[100] = [dict(type=19, npc_id=2, map_id=0)]
        self.assertEqual('no_reference_npc_marker', self.status())

    def test_reused_completion_id_with_another_target_is_deferred(self):
        self.ref[101]['terms'][0]['dwTermID'] = 999
        self.assertEqual('completion_graph_differs', self.status())

    def test_disabled_reference_mission_is_not_reactivated(self):
        self.ref[100]['bType'] = 0
        self.assertEqual('mission_identity_differs', self.status())

    def test_objective_differences_prevent_a_partial_version_mix(self):
        self.ref[100]['terms'].append(dict(bTermType=3, dwTermID=5, bCount=1))
        self.assertEqual('mission_objectives_differ', self.status())

    def test_multiple_completion_actions_require_manual_mapping(self):
        q = copy.deepcopy(self.ref[101]); q['dwQuestID'] = 102; self.ref[102] = q
        self.assertEqual('multiple_completion_actions', self.status())

    def test_npc_on_a_different_map_is_a_dependency_failure(self):
        self.tables['TNPCCHART'][0]['wMapID'] = 1
        self.assertEqual('target_npc_missing_or_wrong_map', self.status())

    def test_extra_conditions_are_preserved_for_review(self):
        self.tables['TQCONDITIONCHART'].append(dict(dwQuestID=101, bConditionType=14, dwConditionID=1, bCount=0))
        self.assertEqual('completion_conditions_need_review', self.status())

    def test_reference_npc_country_conflict_blocks_automatic_repair(self):
        report = analyze(self.tables, self.ref, self.nav)
        add_npc_evidence(report, self.tables, {2: [dict(name='Other country', wMapID=0, bCountryID=1)]})
        self.assertEqual('target_npc_reference_identity_differs', report['rows'][0]['status'])

    def test_duplicate_sql_identity_is_not_silently_overwritten(self):
        self.tables['TQUESTCHART'].append(copy.deepcopy(self.tables['TQUESTCHART'][1]))
        with self.assertRaises(ValueError):
            self.status()


if __name__ == '__main__':
    unittest.main()
